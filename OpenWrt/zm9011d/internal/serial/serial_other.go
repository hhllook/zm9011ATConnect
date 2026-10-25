//go:build !linux

// Package serial 在非 Linux 平台提供占位实现，仅用于本地编译与页面预览。
package serial

import "errors"

// Port 是串口占位类型。
type Port struct {
	name string
	baud int
}

// Open 在非 Linux 平台不可用。
func Open(name string, baud int) (*Port, error) {
	return nil, errors.New("串口功能仅支持 Linux/OpenWrt")
}

// Discover 在非 Linux 平台不可用。
func Discover(baud int) (string, error) {
	return "", errors.New("串口功能仅支持 Linux/OpenWrt")
}

// Read 占位实现。
func (p *Port) Read(b []byte) (int, error) {
	return 0, errors.New("串口功能仅支持 Linux/OpenWrt")
}

// Write 占位实现。
func (p *Port) Write(b []byte) (int, error) {
	return 0, errors.New("串口功能仅支持 Linux/OpenWrt")
}

// Close 占位实现。
func (p *Port) Close() error { return nil }

// Name 返回设备名。
func (p *Port) Name() string { return p.name }

// Baud 返回波特率。
func (p *Port) Baud() int { return p.baud }
