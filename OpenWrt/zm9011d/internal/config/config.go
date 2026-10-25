// Package config 解析 OpenWrt UCI 配置 /etc/config/zm9011。
package config

import (
	"bufio"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"
)

// Config 是守护进程运行参数。
type Config struct {
	// Device 串口设备，"auto" 表示自动探测 AT 口。
	Device string
	// Baud 波特率。
	Baud int
	// Listen HTTP 监听地址。
	Listen string
	// Port HTTP 端口。
	Port int
	// Password 非空时启用 HTTP Basic Auth（内网也建议设置）。
	Password string
	// DeleteAfterRead 读取成功后删除模组内短信。
	DeleteAfterRead bool
	// DataDir 本地数据目录（短信 JSON、日志等）。
	DataDir string
}

// Default 返回默认配置。
func Default() Config {
	return Config{
		Device:          "auto",
		Baud:            115200,
		Listen:          "0.0.0.0",
		Port:            8080,
		DeleteAfterRead: true,
		DataDir:         "/etc/zm9011",
	}
}

// Load 读取 UCI 配置；文件不存在时返回默认值。
func Load(path string) (Config, error) {
	c := Default()
	f, err := os.Open(path)
	if err != nil {
		if os.IsNotExist(err) {
			return c, nil
		}
		return c, err
	}
	defer f.Close()

	return Parse(f, c)
}

// Parse 解析 UCI 文本并覆盖 base 中的配置。
func Parse(r io.Reader, base Config) (Config, error) {
	c := base

	scanner := bufio.NewScanner(r)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}

		fields := strings.Fields(line)
		if len(fields) < 2 || fields[0] != "option" {
			continue
		}

		key := fields[1]
		rest := strings.TrimSpace(strings.TrimPrefix(line, "option"))
		rest = strings.TrimSpace(strings.TrimPrefix(rest, key))
		value := strings.Trim(strings.TrimSpace(rest), "'\"")

		switch key {
		case "device":
			c.Device = value
		case "baud":
			if n, err := strconv.Atoi(value); err == nil && n > 0 {
				c.Baud = n
			}
		case "listen":
			c.Listen = value
		case "port":
			if n, err := strconv.Atoi(value); err == nil && n > 0 && n <= 65535 {
				c.Port = n
			}
		case "password":
			c.Password = value
		case "delete_after_read":
			c.DeleteAfterRead = value == "1" || strings.EqualFold(value, "true") || strings.EqualFold(value, "yes")
		case "data_dir":
			c.DataDir = value
		}
	}
	if err := scanner.Err(); err != nil {
		return c, err
	}

	if strings.TrimSpace(c.Device) == "" {
		c.Device = "auto"
	}
	if c.Listen == "" {
		c.Listen = "0.0.0.0"
	}
	if c.Port <= 0 || c.Port > 65535 {
		c.Port = 8080
	}
	if c.Baud <= 0 {
		c.Baud = 115200
	}
	if strings.TrimSpace(c.DataDir) == "" {
		c.DataDir = "/etc/zm9011"
	}

	return c, nil
}

// Addr 返回 HTTP 监听地址。
func (c Config) Addr() string {
	return fmt.Sprintf("%s:%d", c.Listen, c.Port)
}
