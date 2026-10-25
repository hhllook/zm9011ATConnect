//go:build linux

// Package serial 提供 Linux 串口打开、配置与 AT 口自动探测。
package serial

import (
	"errors"
	"fmt"
	"os"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"
)

// Port 是一个已配置为 raw 8N1 的串口。
type Port struct {
	file      *os.File
	name      string
	baud      int
	closeOnce sync.Once
}

// Open 打开并配置串口。
func Open(name string, baud int) (*Port, error) {
	f, err := os.OpenFile(name, os.O_RDWR|syscall.O_NOCTTY, 0)
	if err != nil {
		return nil, err
	}

	p := &Port{file: f, name: name, baud: baud}
	if err := p.configure(); err != nil {
		_ = f.Close()
		return nil, err
	}
	return p, nil
}

func (p *Port) configure() error {
	fd := p.file.Fd()

	var tio syscall.Termios
	if err := ioctl(fd, syscall.TCGETS, unsafe.Pointer(&tio)); err != nil {
		return fmt.Errorf("tcgetattr: %w", err)
	}

	speed, ok := baudConst(p.baud)
	if !ok {
		return fmt.Errorf("不支持的波特率 %d", p.baud)
	}

	tio.Iflag = 0
	tio.Oflag = 0
	tio.Lflag = 0
	tio.Cflag = speed | syscall.CS8 | syscall.CREAD | syscall.CLOCAL
	tio.Ispeed = speed
	tio.Ospeed = speed
	tio.Cc[syscall.VMIN] = 1
	tio.Cc[syscall.VTIME] = 0

	if err := ioctl(fd, syscall.TCSETS, unsafe.Pointer(&tio)); err != nil {
		return fmt.Errorf("tcsetattr: %w", err)
	}
	return nil
}

// Read 读取串口数据。
func (p *Port) Read(b []byte) (int, error) { return p.file.Read(b) }

// Write 写入串口数据。
func (p *Port) Write(b []byte) (int, error) { return p.file.Write(b) }

// Close 关闭串口。
func (p *Port) Close() error {
	var err error
	p.closeOnce.Do(func() { err = p.file.Close() })
	return err
}

// Name 返回设备路径。
func (p *Port) Name() string { return p.name }

// Baud 返回波特率。
func (p *Port) Baud() int { return p.baud }

// Discover 扫描常见 USB 串口设备，发送 AT 并等待响应，返回可用的 AT 口。
func Discover(baud int) (string, error) {
	candidates := make([]string, 0, 16)
	for i := 0; i <= 7; i++ {
		candidates = append(candidates, fmt.Sprintf("/dev/ttyUSB%d", i))
	}
	for i := 0; i <= 3; i++ {
		candidates = append(candidates, fmt.Sprintf("/dev/ttyACM%d", i))
	}

	var tried []string
	for _, dev := range candidates {
		if _, err := os.Stat(dev); err != nil {
			continue
		}
		tried = append(tried, dev)
		if probeAT(dev, baud) {
			return dev, nil
		}
	}

	if len(tried) == 0 {
		return "", errors.New("未找到 /dev/ttyUSB* 或 /dev/ttyACM* 设备")
	}
	return "", fmt.Errorf("以下设备均未响应 AT：%s", strings.Join(tried, ", "))
}

// probeAT 打开设备发送 AT，若收到 OK/ERROR 等响应则认为它是 AT 口。
func probeAT(device string, baud int) bool {
	p, err := Open(device, baud)
	if err != nil {
		return false
	}

	if _, err := p.Write([]byte("AT\r")); err != nil {
		_ = p.Close()
		return false
	}

	dataCh := make(chan string, 1)
	go func() {
		buf := make([]byte, 512)
		var sb strings.Builder
		deadline := time.Now().Add(700 * time.Millisecond)
		for time.Now().Before(deadline) {
			n, err := p.Read(buf)
			if n > 0 {
				sb.Write(buf[:n])
				upper := strings.ToUpper(sb.String())
				if strings.Contains(upper, "OK") || strings.Contains(upper, "ERROR") {
					break
				}
			}
			if err != nil {
				break
			}
		}
		dataCh <- sb.String()
	}()

	var data string
	select {
	case data = <-dataCh:
	case <-time.After(900 * time.Millisecond):
	}

	_ = p.Close()

	upper := strings.ToUpper(data)
	return strings.Contains(upper, "OK") || strings.Contains(upper, "ERROR") || strings.Contains(upper, "+CME")
}

func ioctl(fd uintptr, req uintptr, arg unsafe.Pointer) error {
	_, _, errno := syscall.Syscall6(syscall.SYS_IOCTL, fd, req, uintptr(arg), 0, 0, 0)
	if errno != 0 {
		return errno
	}
	return nil
}

func baudConst(baud int) (uint32, bool) {
	// Linux asm-generic/termbits.h 中的标准波特率常量
	table := map[int]uint32{
		1200:   0x00000009,
		2400:   0x0000000B,
		4800:   0x0000000C,
		9600:   0x0000000D,
		19200:  0x0000000E,
		38400:  0x0000000F,
		57600:  0x00001001,
		115200: 0x00001002,
		230400: 0x00001003,
		460800: 0x00001004,
		500000: 0x00001005,
		576000: 0x00001006,
		921600: 0x00001007,
	}
	v, ok := table[baud]
	return v, ok
}
