// Package store 负责短信本地持久化（JSON，带原子写入）。
package store

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"sync"
	"time"

	"zm9011d/internal/logbuf"
)

// Message 是一条短信（接收或发送）。
type Message struct {
	ID          string `json:"id"`
	Sender      string `json:"sender"`
	Body        string `json:"body"`
	Time        string `json:"time"`    // 短信中心时间，展示用，不做时区转换
	SortKey     int64  `json:"sortKey"` // Unix 秒，排序用
	Outgoing    bool   `json:"outgoing"`
	Read        bool   `json:"read"`
	Code        string `json:"code,omitempty"`
	Tag         string `json:"tag,omitempty"`
	ServiceName string `json:"serviceName,omitempty"`
	Raw         string `json:"raw,omitempty"`
	Storage     string `json:"storage,omitempty"`
	Index       int    `json:"index,omitempty"`
}

// Store 是线程安全的短信集合。
type Store struct {
	mu       sync.RWMutex
	path     string
	messages []Message
	max      int
	log      *logbuf.Buffer
}

// New 创建并加载本地短信库。
func New(dir string, log *logbuf.Buffer) (*Store, error) {
	if err := os.MkdirAll(dir, 0755); err != nil {
		return nil, err
	}

	s := &Store{
		path: filepath.Join(dir, "messages.json"),
		max:  5000,
		log:  log,
	}

	if err := s.load(); err != nil {
		log.Error("[store.New] 读取本地短信失败 path=%s error=%v", s.path, err)
	}

	return s, nil
}

func (s *Store) load() error {
	data, err := os.ReadFile(s.path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	if len(data) == 0 {
		return nil
	}

	var messages []Message
	if err := json.Unmarshal(data, &messages); err != nil {
		// 保留损坏文件，避免静默丢数据
		badPath := s.path + ".bad"
		_ = os.Rename(s.path, badPath)
		return err
	}

	sort.SliceStable(messages, func(i, j int) bool { return messages[i].SortKey < messages[j].SortKey })
	s.messages = messages
	return nil
}

// Add 加入一条短信；重复消息返回 added=false。
func (s *Store) Add(m Message) (Message, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	if m.ID == "" {
		m.ID = NewID()
	}
	if m.SortKey == 0 {
		m.SortKey = time.Now().Unix()
	}
	if m.Time == "" {
		m.Time = time.Unix(m.SortKey, 0).Format("2006-01-02 15:04:05")
	}

	for _, e := range s.messages {
		if !e.Outgoing && !m.Outgoing &&
			e.Sender == m.Sender && e.Body == m.Body &&
			abs(e.SortKey-m.SortKey) <= 120 {
			return e, false, nil
		}
	}

	s.messages = append(s.messages, m)
	if len(s.messages) > s.max {
		s.messages = append([]Message(nil), s.messages[len(s.messages)-s.max:]...)
	}

	return m, true, s.saveLocked()
}

// List 返回按时间正序的消息副本。
func (s *Store) List() []Message {
	s.mu.RLock()
	defer s.mu.RUnlock()

	out := make([]Message, len(s.messages))
	copy(out, s.messages)
	return out
}

// Count 返回消息数量。
func (s *Store) Count() int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return len(s.messages)
}

// MarkRead 标记指定消息为已读。
func (s *Store) MarkRead(ids []string) error {
	if len(ids) == 0 {
		return nil
	}
	idSet := make(map[string]struct{}, len(ids))
	for _, id := range ids {
		idSet[id] = struct{}{}
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	changed := false
	for i := range s.messages {
		if _, ok := idSet[s.messages[i].ID]; ok && !s.messages[i].Read {
			s.messages[i].Read = true
			changed = true
		}
	}
	if !changed {
		return nil
	}
	return s.saveLocked()
}

// Delete 删除指定消息。
func (s *Store) Delete(ids []string) error {
	if len(ids) == 0 {
		return nil
	}
	idSet := make(map[string]struct{}, len(ids))
	for _, id := range ids {
		idSet[id] = struct{}{}
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	kept := s.messages[:0]
	for _, m := range s.messages {
		if _, ok := idSet[m.ID]; !ok {
			kept = append(kept, m)
		}
	}
	s.messages = kept
	return s.saveLocked()
}

// Clear 清空全部短信。
func (s *Store) Clear() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.messages = nil
	return s.saveLocked()
}

func (s *Store) saveLocked() error {
	data, err := json.MarshalIndent(s.messages, "", "  ")
	if err != nil {
		s.log.Error("[store.saveLocked] 序列化短信失败 count=%d error=%v", len(s.messages), err)
		return err
	}

	tmp := s.path + ".tmp"
	if err := os.WriteFile(tmp, data, 0644); err != nil {
		s.log.Error("[store.saveLocked] 写入临时文件失败 path=%s error=%v", tmp, err)
		return err
	}
	if err := os.Rename(tmp, s.path); err != nil {
		s.log.Error("[store.saveLocked] 原子替换失败 path=%s error=%v", s.path, err)
		return err
	}
	return nil
}

// NewID 生成短随机 ID。
func NewID() string {
	buf := make([]byte, 8)
	if _, err := rand.Read(buf); err != nil {
		return time.Now().Format("20060102150405.000000000")
	}
	return hex.EncodeToString(buf)
}

func abs(v int64) int64 {
	if v < 0 {
		return -v
	}
	return v
}
