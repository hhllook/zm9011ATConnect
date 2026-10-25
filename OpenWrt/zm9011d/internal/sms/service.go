package sms

import (
	"context"
	"errors"
	"fmt"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	"zm9011d/internal/at"
	"zm9011d/internal/config"
	"zm9011d/internal/logbuf"
	"zm9011d/internal/store"
)

var cmtiPattern = regexp.MustCompile(`^\+CMTI:\s*"([^"]+)"\s*,\s*(\d+)`)

// Service 负责 +CMTI 处理、短信读取、发送与本地存储。
type Service struct {
	at    *at.Client
	store *store.Store
	log   *logbuf.Buffer
	cfg   config.Config

	readMu sync.Mutex

	subMu sync.RWMutex
	subs  map[chan store.Message]struct{}
}

// New 创建短信服务。
func New(client *at.Client, st *store.Store, cfg config.Config, log *logbuf.Buffer) *Service {
	return &Service{
		at:    client,
		store: st,
		log:   log,
		cfg:   cfg,
		subs:  make(map[chan store.Message]struct{}),
	}
}

// Start 订阅 URC，实时读取新短信。
func (s *Service) Start(ctx context.Context) {
	ch, cancel := s.at.SubscribeURC()

	go func() {
		defer cancel()
		for {
			select {
			case <-ctx.Done():
				return
			case line, ok := <-ch:
				if !ok {
					return
				}
				if !strings.HasPrefix(strings.ToUpper(line), "+CMTI:") {
					continue
				}

				m := cmtiPattern.FindStringSubmatch(line)
				if len(m) != 3 {
					s.log.Warn("[sms.Service.Start] +CMTI 格式无法解析 line=%s", line)
					continue
				}
				index, err := strconv.Atoi(m[2])
				if err != nil {
					s.log.Warn("[sms.Service.Start] +CMTI 索引非法 line=%s", line)
					continue
				}

				go s.readMessage(ctx, m[1], index)
			}
		}
	}()
}

// Subscribe 订阅新短信（接收或发送）。取消函数必须调用。
func (s *Service) Subscribe() (<-chan store.Message, func()) {
	ch := make(chan store.Message, 32)

	s.subMu.Lock()
	s.subs[ch] = struct{}{}
	s.subMu.Unlock()

	cancel := func() {
		s.subMu.Lock()
		if _, ok := s.subs[ch]; ok {
			delete(s.subs, ch)
			close(ch)
		}
		s.subMu.Unlock()
	}
	return ch, cancel
}

func (s *Service) broadcast(m store.Message) {
	s.subMu.Lock()
	defer s.subMu.Unlock()
	for ch := range s.subs {
		select {
		case ch <- m:
		default:
		}
	}
}

// List 返回短信列表。
func (s *Service) List() []store.Message { return s.store.List() }

// MarkRead 标记已读。
func (s *Service) MarkRead(ids []string) error { return s.store.MarkRead(ids) }

// Delete 删除短信。
func (s *Service) Delete(ids []string) error { return s.store.Delete(ids) }

// Clear 清空全部短信。
func (s *Service) Clear() error { return s.store.Clear() }

// Send 发送短信（中文自动 UCS2），成功后写入本地并广播。
func (s *Service) Send(ctx context.Context, sender, text string) (store.Message, error) {
	sender = strings.TrimSpace(sender)
	text = strings.TrimSpace(text)
	if !validNumber(sender) {
		return store.Message{}, errors.New("接收方号码非法")
	}
	if text == "" {
		return store.Message{}, errors.New("短信内容为空")
	}

	mode, err := s.at.Send(ctx, "AT+CMGF=1", 5*time.Second)
	if err != nil || !mode.Success {
		return store.Message{}, fmt.Errorf("切换到 TEXT 模式失败: %s %v", mode.Final, err)
	}

	useUCS2 := containsNonASCII(text)
	if useUCS2 {
		csmp, err := s.at.Send(ctx, "AT+CSMP=17,255,0,8", 5*time.Second)
		if err != nil || !csmp.Success {
			return store.Message{}, fmt.Errorf("设置 UCS2 编码失败: %s %v", csmp.Final, err)
		}
	}

	body := text
	if useUCS2 {
		body = ToUCS2Hex(text)
	}

	res, err := s.at.SendInteractive(ctx, fmt.Sprintf("AT+CMGS=\"%s\"", sender), body, 20*time.Second)

	if useUCS2 {
		_, _ = s.at.Send(ctx, "AT+CSMP=,,0,0", 5*time.Second)
	}
	// 恢复 PDU 模式，保证后续 +CMTI 读取
	_, _ = s.at.Send(ctx, "AT+CMGF=0", 5*time.Second)

	if err != nil {
		return store.Message{}, fmt.Errorf("发送短信失败: %w", err)
	}
	if !res.Success {
		return store.Message{}, fmt.Errorf("发送短信失败: %s", res.Final)
	}

	now := time.Now()
	msg := store.Message{
		Sender:   sender,
		Body:     text,
		Time:     now.Format("2006-01-02 15:04:05"),
		SortKey:  now.Unix(),
		Outgoing: true,
		Read:     true,
		Raw:      text,
	}

	stored, added, err := s.store.Add(msg)
	if err != nil {
		s.log.Error("[sms.Service.Send] 保存已发送短信失败 sender=%s error=%v", sender, err)
	}
	if added {
		s.broadcast(stored)
	}
	s.log.Info("[sms.Service.Send] 短信已发送 sender=%s length=%d ucs2=%v", sender, len([]rune(text)), useUCS2)
	return stored, err
}

// readMessage 读取指定索引短信（PDU 优先）。
func (s *Service) readMessage(ctx context.Context, storage string, index int) {
	s.readMu.Lock()
	defer s.readMu.Unlock()

	res, err := s.at.Send(ctx, fmt.Sprintf("AT+CMGR=%d", index), 15*time.Second)
	if err != nil || !res.Success {
		s.log.Error("[sms.Service.readMessage] 读取短信失败 storage=%s index=%d final=%s error=%v", storage, index, res.Final, err)
		return
	}

	msg, err := s.parseCMGR(res, storage, index)
	if err != nil {
		s.log.Warn("[sms.Service.readMessage] 解析短信失败 storage=%s index=%d error=%v lines=%s", storage, index, err, strings.Join(res.Lines, " | "))
		return
	}

	a := Analyze(msg.Body)
	msg.Code = a.Code
	msg.Tag = a.Tag
	msg.ServiceName = a.ServiceName

	stored, added, err := s.store.Add(msg)
	if err != nil {
		s.log.Error("[sms.Service.readMessage] 保存短信失败 sender=%s index=%d error=%v", msg.Sender, index, err)
		return
	}
	if !added {
		s.log.Info("[sms.Service.readMessage] 跳过重复短信 sender=%s index=%d", msg.Sender, index)
		return
	}

	s.broadcast(stored)
	s.log.Info("[sms.Service.readMessage] 收到短信 sender=%s index=%d code=%s", stored.Sender, index, stored.Code)

	if s.cfg.DeleteAfterRead {
		del, err := s.at.Send(ctx, fmt.Sprintf("AT+CMGD=%d", index), 5*time.Second)
		if err != nil || !del.Success {
			s.log.Warn("[sms.Service.readMessage] 删除模组短信失败 index=%d final=%s error=%v", index, del.Final, err)
		}
	}
}

func (s *Service) parseCMGR(res at.Result, storage string, index int) (store.Message, error) {
	if len(res.Lines) == 0 {
		return store.Message{}, errors.New("响应为空")
	}

	headerIndex := -1
	for i, line := range res.Lines {
		if strings.HasPrefix(strings.ToUpper(strings.TrimSpace(line)), "+CMGR:") {
			headerIndex = i
			break
		}
	}
	if headerIndex < 0 {
		return store.Message{}, errors.New("缺少 +CMGR 头")
	}

	header := strings.TrimSpace(res.Lines[headerIndex])
	raw := strings.Join(res.Lines, "\n")

	if strings.Contains(header, "\"") {
		return parseTextCMGR(header, res.Lines[headerIndex+1:], raw, storage, index)
	}

	for _, line := range res.Lines[headerIndex+1:] {
		line = strings.TrimSpace(line)
		if !isHexLine(line) {
			continue
		}
		decoded, err := DecodeDeliver(line)
		if err != nil {
			continue
		}

		ts := time.Now()
		if decoded.HasTimestamp {
			ts = decoded.Timestamp
		}

		return store.Message{
			Sender:  normalizeSender(decoded.Sender),
			Body:    decoded.Body,
			Time:    ts.Format("2006-01-02 15:04:05"),
			SortKey: ts.Unix(),
			Raw:     raw,
			Storage: storage,
			Index:   index,
		}, nil
	}

	return store.Message{}, errors.New("未找到有效 PDU")
}

var quotedPattern = regexp.MustCompile(`"([^"]*)"`)

func parseTextCMGR(header string, bodyLines []string, raw, storage string, index int) (store.Message, error) {
	fields := quotedPattern.FindAllStringSubmatch(header, -1)
	sender := ""
	if len(fields) > 1 {
		sender = fields[1][1]
	}

	ts := time.Now()
	if len(fields) > 3 {
		if parsed, err := time.Parse("06/01/02,15:04:05", trimTo(fields[3][1], 17)); err == nil {
			ts = parsed
		}
	}

	body := strings.TrimSpace(strings.Join(bodyLines, "\n"))
	if looksLikeUCS2Hex(body) {
		body = DecodeUCS2Hex(body)
	}

	return store.Message{
		Sender:  normalizeSender(sender),
		Body:    body,
		Time:    ts.Format("2006-01-02 15:04:05"),
		SortKey: ts.Unix(),
		Raw:     raw,
		Storage: storage,
		Index:   index,
	}, nil
}

func trimTo(s string, n int) string {
	if len(s) > n {
		return s[:n]
	}
	return s
}

func normalizeSender(sender string) string {
	sender = strings.TrimSpace(sender)
	if sender == "" {
		return "未知号码"
	}
	return sender
}

func isHexLine(line string) bool {
	if len(line) < 10 || len(line)%2 != 0 {
		return false
	}
	for _, ch := range line {
		switch {
		case ch >= '0' && ch <= '9', ch >= 'a' && ch <= 'f', ch >= 'A' && ch <= 'F':
		default:
			return false
		}
	}
	return true
}

func looksLikeUCS2Hex(value string) bool {
	if len(value) < 4 || len(value)%4 != 0 {
		return false
	}
	for _, ch := range value {
		switch {
		case ch >= '0' && ch <= '9', ch >= 'a' && ch <= 'f', ch >= 'A' && ch <= 'F':
		default:
			return false
		}
	}
	return true
}

func containsNonASCII(text string) bool {
	for _, r := range text {
		if r > 0x7F {
			return true
		}
	}
	return false
}

func validNumber(number string) bool {
	if number == "" {
		return false
	}
	for _, r := range number {
		switch {
		case r >= '0' && r <= '9', r == '+', r == '*', r == '#':
		default:
			return false
		}
	}
	return true
}
