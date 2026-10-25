// Package logbuf 提供内存日志缓冲与实时订阅（供 WebSocket/SSE 推送）。
package logbuf

import (
	"fmt"
	"sync"
	"time"
)

// Entry 是一条日志。
type Entry struct {
	Time  string `json:"time"`
	Level string `json:"level"`
	Text  string `json:"text"`
}

// Buffer 是线程安全的环形日志缓冲。
type Buffer struct {
	mu      sync.RWMutex
	entries []Entry
	max     int
	subs    map[chan Entry]struct{}
}

// New 创建日志缓冲。
func New(max int) *Buffer {
	if max <= 0 {
		max = 1000
	}
	return &Buffer{
		max:  max,
		subs: make(map[chan Entry]struct{}),
	}
}

// Add 写入一条日志并广播给订阅者。
func (b *Buffer) Add(level, text string) {
	entry := Entry{
		Time:  time.Now().Format("2006-01-02 15:04:05"),
		Level: level,
		Text:  text,
	}

	b.mu.Lock()
	defer b.mu.Unlock()

	b.entries = append(b.entries, entry)
	if len(b.entries) > b.max {
		b.entries = append([]Entry(nil), b.entries[len(b.entries)-b.max:]...)
	}

	for ch := range b.subs {
		select {
		case ch <- entry:
		default:
			// 慢客户端丢弃日志，避免阻塞串口线程
		}
	}
}

// Snapshot 返回日志快照（时间正序）。
func (b *Buffer) Snapshot() []Entry {
	b.mu.RLock()
	defer b.mu.RUnlock()

	out := make([]Entry, len(b.entries))
	copy(out, b.entries)
	return out
}

// Subscribe 订阅新日志。返回取消函数，必须调用以释放资源。
func (b *Buffer) Subscribe() (<-chan Entry, func()) {
	ch := make(chan Entry, 128)

	b.mu.Lock()
	b.subs[ch] = struct{}{}
	b.mu.Unlock()

	cancel := func() {
		b.mu.Lock()
		if _, ok := b.subs[ch]; ok {
			delete(b.subs, ch)
			close(ch)
		}
		b.mu.Unlock()
	}
	return ch, cancel
}

func (b *Buffer) Info(format string, args ...any)  { b.Add("info", fmt.Sprintf(format, args...)) }
func (b *Buffer) Tx(format string, args ...any)    { b.Add("tx", fmt.Sprintf(format, args...)) }
func (b *Buffer) Rx(format string, args ...any)    { b.Add("rx", fmt.Sprintf(format, args...)) }
func (b *Buffer) Urc(format string, args ...any)   { b.Add("urc", fmt.Sprintf(format, args...)) }
func (b *Buffer) Warn(format string, args ...any)  { b.Add("warn", fmt.Sprintf(format, args...)) }
func (b *Buffer) Error(format string, args ...any) { b.Add("error", fmt.Sprintf(format, args...)) }
