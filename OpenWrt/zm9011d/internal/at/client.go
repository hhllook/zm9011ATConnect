// Package at 提供串口 AT 命令队列、响应解析、URC 分发与自动重连。
package at

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"zm9011d/internal/config"
	"zm9011d/internal/logbuf"
	"zm9011d/internal/serial"
)

// Result 是一次 AT 命令的执行结果。
type Result struct {
	Lines   []string `json:"lines"`
	Final   string   `json:"final"`
	Success bool     `json:"success"`
	Timeout bool     `json:"timeout"`
	Err     string   `json:"error,omitempty"`
}

// Status 是串口连接状态。
type Status struct {
	Connected bool   `json:"connected"`
	Device    string `json:"device"`
	Baud      int    `json:"baud"`
	Realtime  bool   `json:"realtime"`
	SimSlot   string `json:"simSlot"`
}

type pending struct {
	cmd string
	ch  chan string
}

// Client 是 AT 串口客户端。
type Client struct {
	cfg config.Config
	log *logbuf.Buffer

	portMu sync.RWMutex
	port   *serial.Port

	cmdMu sync.Mutex

	pendingMu sync.Mutex
	pending   *pending

	urcMu   sync.Mutex
	urcSubs map[chan string]struct{}

	statusMu sync.RWMutex
	device   string
	simSlot  string

	connected atomic.Bool
	realtime  atomic.Bool
}

// New 创建 AT 客户端。
func New(cfg config.Config, log *logbuf.Buffer) *Client {
	return &Client{
		cfg:     cfg,
		log:     log,
		urcSubs: make(map[chan string]struct{}),
	}
}

// Run 连接串口并持续重连，直到 ctx 取消。
func (c *Client) Run(ctx context.Context) {
	for ctx.Err() == nil {
		port, device, err := c.openPort()
		if err != nil {
			c.log.Warn("[at.Client.Run] 打开串口失败 device=%s baud=%d error=%v", c.cfg.Device, c.cfg.Baud, err)
			if !sleepCtx(ctx, 3*time.Second) {
				return
			}
			continue
		}

		c.portMu.Lock()
		c.port = port
		c.portMu.Unlock()

		c.statusMu.Lock()
		c.device = device
		c.statusMu.Unlock()

		c.connected.Store(true)
		c.log.Info("[at.Client.Run] 串口已打开 device=%s baud=%d", device, c.cfg.Baud)

		readDone := make(chan struct{})
		go func() {
			defer close(readDone)
			c.readLoop(port)
		}()

		go c.configure(ctx)

		select {
		case <-ctx.Done():
			_ = port.Close()
			return
		case <-readDone:
			c.connected.Store(false)
			c.realtime.Store(false)
			c.portMu.Lock()
			if c.port == port {
				c.port = nil
			}
			c.portMu.Unlock()
			_ = port.Close()
			c.log.Warn("[at.Client.Run] 串口断开 device=%s，3 秒后重连", device)
			if !sleepCtx(ctx, 3*time.Second) {
				return
			}
		}
	}
}

func (c *Client) openPort() (*serial.Port, string, error) {
	// 优先使用配置的固定设备
	if c.cfg.Device != "" && c.cfg.Device != "auto" {
		port, err := serial.Open(c.cfg.Device, c.cfg.Baud)
		if err == nil {
			return port, c.cfg.Device, nil
		}
		c.log.Warn("[at.Client.openPort] 配置的串口不可用 device=%s error=%v，尝试自动探测", c.cfg.Device, err)
	}

	device, err := serial.Discover(c.cfg.Baud)
	if err != nil {
		return nil, "", err
	}

	port, err := serial.Open(device, c.cfg.Baud)
	if err != nil {
		return nil, "", err
	}
	return port, device, nil
}

// configure 开启实时短信接收；失败时每 30 秒重试。
func (c *Client) configure(ctx context.Context) {
	for {
		if ctx.Err() != nil || !c.connected.Load() {
			return
		}

		if c.configureOnce(ctx) {
			return
		}
		if !sleepCtx(ctx, 30*time.Second) {
			return
		}
	}
}

func (c *Client) configureOnce(ctx context.Context) bool {
	mode, err := c.Send(ctx, "AT+CMGF=0", 5*time.Second)
	if err != nil || !mode.Success {
		c.log.Error("[at.Client.configureOnce] 切换 PDU 模式失败 final=%s error=%v", mode.Final, err)
		return false
	}

	cnmi, err := c.Send(ctx, "AT+CNMI=2,1,0,0,0", 5*time.Second)
	if err != nil || !cnmi.Success {
		c.log.Error("[at.Client.configureOnce] 开启新短信上报失败 final=%s error=%v", cnmi.Final, err)
		return false
	}

	c.realtime.Store(true)
	c.log.Info("[at.Client.configureOnce] 实时短信已开启（PDU 模式）")

	c.querySimSlot(ctx)
	return true
}

func (c *Client) querySimSlot(ctx context.Context) {
	res, err := c.Send(ctx, "AT+ZSWSLOT?", 3*time.Second)
	if err != nil || !res.Success {
		return
	}
	for _, line := range res.Lines {
		if strings.HasPrefix(strings.ToUpper(line), "+ZSWSLOT:") {
			value := strings.TrimSpace(line[len("+ZSWSLOT:"):])
			if value != "" {
				c.statusMu.Lock()
				c.simSlot = value
				c.statusMu.Unlock()
				return
			}
		}
	}
}

func (c *Client) readLoop(port *serial.Port) {
	buf := make([]byte, 4096)
	var lineBuf strings.Builder

	for {
		n, err := port.Read(buf)
		if n > 0 {
			lineBuf.Write(buf[:n])
			for {
				line, ok := takeLine(&lineBuf)
				if !ok {
					break
				}
				c.handleLine(line)
			}
		}
		if err != nil {
			return
		}
	}
}

func takeLine(sb *strings.Builder) (string, bool) {
	s := sb.String()
	idx := strings.IndexAny(s, "\r\n")
	if idx < 0 {
		return "", false
	}

	line := s[:idx]
	rest := s[idx+1:]
	if len(rest) > 0 && (rest[0] == '\r' || rest[0] == '\n') {
		rest = rest[1:]
	}
	sb.Reset()
	sb.WriteString(rest)
	return strings.TrimSpace(line), true
}

func (c *Client) handleLine(line string) {
	if line == "" {
		return
	}

	if isURC(line) {
		c.log.Urc("%s", line)
		c.broadcastURC(line)
		return
	}

	c.log.Rx("%s", line)
	c.routeToPending(line)
}

func (c *Client) routeToPending(line string) {
	c.pendingMu.Lock()
	pd := c.pending
	c.pendingMu.Unlock()
	if pd == nil {
		return
	}

	select {
	case pd.ch <- line:
	default:
	}
}

func (c *Client) setPending(pd *pending) {
	c.pendingMu.Lock()
	c.pending = pd
	c.pendingMu.Unlock()
}

func (c *Client) clearPending(pd *pending) {
	c.pendingMu.Lock()
	if c.pending == pd {
		c.pending = nil
	}
	c.pendingMu.Unlock()
}

// Send 发送一条 AT 命令并等待终止符（OK/ERROR/CONNECT/>）。
func (c *Client) Send(ctx context.Context, command string, timeout time.Duration) (Result, error) {
	if !c.connected.Load() {
		return Result{Err: "串口未连接"}, fmt.Errorf("串口未连接")
	}

	c.cmdMu.Lock()
	defer c.cmdMu.Unlock()

	port := c.getPort()
	if port == nil {
		return Result{Err: "串口未打开"}, fmt.Errorf("串口未打开")
	}

	pd := &pending{cmd: command, ch: make(chan string, 256)}
	c.setPending(pd)
	defer c.clearPending(pd)

	c.log.Tx("%s", command)
	if _, err := port.Write([]byte(command + "\r")); err != nil {
		c.log.Error("[at.Client.Send] 写串口失败 command=%s error=%v", command, err)
		_ = port.Close()
		return Result{Err: err.Error()}, err
	}

	timer := time.NewTimer(timeout)
	defer timer.Stop()

	var lines []string
	for {
		select {
		case <-ctx.Done():
			return Result{Lines: lines, Err: ctx.Err().Error()}, ctx.Err()
		case <-timer.C:
			return Result{Lines: lines, Timeout: true, Err: "响应超时"}, nil
		case line := <-pd.ch:
			if strings.EqualFold(line, command) {
				continue
			}
			if line == ">" {
				return Result{Lines: lines, Final: ">", Success: true}, nil
			}
			if isFinalLine(line) {
				success := strings.EqualFold(line, "OK") || strings.EqualFold(line, "CONNECT")
				return Result{Lines: lines, Final: line, Success: success}, nil
			}
			lines = append(lines, line)
		}
	}
}

// SendInteractive 发送两段式命令（如 AT+CMGS）：等待 > 提示符后发送正文并以 Ctrl-Z 结束。
func (c *Client) SendInteractive(ctx context.Context, command, body string, timeout time.Duration) (Result, error) {
	if !c.connected.Load() {
		return Result{Err: "串口未连接"}, fmt.Errorf("串口未连接")
	}

	c.cmdMu.Lock()
	defer c.cmdMu.Unlock()

	port := c.getPort()
	if port == nil {
		return Result{Err: "串口未打开"}, fmt.Errorf("串口未打开")
	}

	pd := &pending{cmd: command, ch: make(chan string, 256)}
	c.setPending(pd)
	defer c.clearPending(pd)

	c.log.Tx("%s", command)
	if _, err := port.Write([]byte(command + "\r")); err != nil {
		c.log.Error("[at.Client.SendInteractive] 写命令失败 command=%s error=%v", command, err)
		_ = port.Close()
		return Result{Err: err.Error()}, err
	}

	timer := time.NewTimer(timeout)
	defer timer.Stop()

	var lines []string
	waitingBody := true
	for {
		select {
		case <-ctx.Done():
			return Result{Lines: lines, Err: ctx.Err().Error()}, ctx.Err()
		case <-timer.C:
			return Result{Lines: lines, Timeout: true, Err: "响应超时"}, nil
		case line := <-pd.ch:
			if strings.EqualFold(line, command) {
				continue
			}

			if waitingBody {
				if line == ">" {
					waitingBody = false
					c.log.Tx("%s<CTRL-Z>", body)
					if _, err := port.Write([]byte(body + "\x1a")); err != nil {
						c.log.Error("[at.Client.SendInteractive] 写正文失败 command=%s error=%v", command, err)
						_ = port.Close()
						return Result{Lines: lines, Err: err.Error()}, err
					}
					continue
				}
				if isFinalLine(line) {
					success := strings.EqualFold(line, "OK")
					return Result{Lines: lines, Final: line, Success: success}, nil
				}
				lines = append(lines, line)
				continue
			}

			if isFinalLine(line) {
				success := strings.EqualFold(line, "OK") || strings.EqualFold(line, "CONNECT")
				return Result{Lines: lines, Final: line, Success: success}, nil
			}
			lines = append(lines, line)
		}
	}
}

// SubscribeURC 订阅主动上报（+CMTI 等）。取消函数必须调用。
func (c *Client) SubscribeURC() (<-chan string, func()) {
	ch := make(chan string, 64)

	c.urcMu.Lock()
	c.urcSubs[ch] = struct{}{}
	c.urcMu.Unlock()

	cancel := func() {
		c.urcMu.Lock()
		if _, ok := c.urcSubs[ch]; ok {
			delete(c.urcSubs, ch)
			close(ch)
		}
		c.urcMu.Unlock()
	}
	return ch, cancel
}

func (c *Client) broadcastURC(line string) {
	c.urcMu.Lock()
	defer c.urcMu.Unlock()

	for ch := range c.urcSubs {
		select {
		case ch <- line:
		default:
		}
	}
}

// Status 返回当前连接状态。
func (c *Client) Status() Status {
	c.statusMu.RLock()
	device := c.device
	simSlot := c.simSlot
	c.statusMu.RUnlock()

	return Status{
		Connected: c.connected.Load(),
		Device:    device,
		Baud:      c.cfg.Baud,
		Realtime:  c.realtime.Load(),
		SimSlot:   simSlot,
	}
}

func (c *Client) getPort() *serial.Port {
	c.portMu.RLock()
	defer c.portMu.RUnlock()
	return c.port
}

func isFinalLine(line string) bool {
	upper := strings.ToUpper(strings.TrimSpace(line))
	return upper == "OK" ||
		upper == "ERROR" ||
		upper == "CONNECT" ||
		strings.HasPrefix(upper, "+CME ERROR") ||
		strings.HasPrefix(upper, "+CMS ERROR") ||
		strings.HasPrefix(upper, "ERROR:")
}

func isURC(line string) bool {
	upper := strings.ToUpper(strings.TrimSpace(line))
	prefixes := []string{
		"+CMTI:", "+CMT:", "+CDS:", "+CDSI:", "+CBM:",
		"+MYURC", "+ZIND:", "+CRING:", "+ZURC:",
	}
	for _, p := range prefixes {
		if strings.HasPrefix(upper, p) {
			return true
		}
	}
	return upper == "RING"
}

func sleepCtx(ctx context.Context, d time.Duration) bool {
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-timer.C:
		return true
	}
}
