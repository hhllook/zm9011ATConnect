// zm9011d：ZM9011 模组 AT 调试/实时短信守护进程（OpenWrt）。
package main

import (
	"context"
	"flag"
	"log"
	"os"
	"os/signal"
	"syscall"
	"time"

	"zm9011d/internal/api"
	"zm9011d/internal/at"
	"zm9011d/internal/config"
	"zm9011d/internal/logbuf"
	"zm9011d/internal/sms"
	"zm9011d/internal/store"
)

var version = "0.1.0"

func main() {
	configPath := flag.String("config", "/etc/config/zm9011", "UCI 配置文件路径")
	device := flag.String("device", "", "串口设备路径，auto 表示自动探测")
	baud := flag.Int("baud", 0, "串口波特率")
	listen := flag.String("listen", "", "HTTP 监听地址")
	port := flag.Int("port", 0, "HTTP 端口")
	dataDir := flag.String("data-dir", "", "数据目录")
	demo := flag.Bool("demo", false, "演示模式：不连接串口，使用示例短信")
	flag.Parse()

	cfg, err := config.Load(*configPath)
	if err != nil {
		log.Printf("读取配置失败: %v", err)
		os.Exit(1)
	}
	if *device != "" {
		cfg.Device = *device
	}
	if *baud > 0 {
		cfg.Baud = *baud
	}
	if *listen != "" {
		cfg.Listen = *listen
	}
	if *port > 0 {
		cfg.Port = *port
	}
	if *dataDir != "" {
		cfg.DataDir = *dataDir
	}

	logs := logbuf.New(1000)
	logs.Info("[main] zm9011d %s 启动 config=%s device=%s baud=%d listen=%s dataDir=%s demo=%v",
		version, *configPath, cfg.Device, cfg.Baud, cfg.Addr(), cfg.DataDir, *demo)

	mirrorErrors(logs)

	st, err := store.New(cfg.DataDir, logs)
	if err != nil {
		logs.Error("[main] 初始化短信存储失败 dir=%s error=%v", cfg.DataDir, err)
		log.Printf("初始化短信存储失败: %v", err)
		os.Exit(1)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	client := at.New(cfg, logs)
	smsSvc := sms.New(client, st, cfg, logs)
	smsSvc.Start(ctx)

	if *demo {
		seedDemoMessages(st, logs)
		logs.Info("[main] 演示模式：未启动串口")
	} else {
		go client.Run(ctx)
	}

	server := api.New(cfg, client, smsSvc, st, logs, version)
	go server.Start(ctx)

	<-ctx.Done()
	logs.Info("[main] 收到退出信号，正在停止")
	time.Sleep(300 * time.Millisecond)
}

// mirrorErrors 把 warn/error 日志同步到标准错误，方便 logread 排查。
func mirrorErrors(logs *logbuf.Buffer) {
	ch, cancel := logs.Subscribe()
	go func() {
		defer cancel()
		for entry := range ch {
			if entry.Level == "warn" || entry.Level == "error" {
				log.Printf("[%s] %s", entry.Level, entry.Text)
			}
		}
	}()
}

func seedDemoMessages(st *store.Store, logs *logbuf.Buffer) {
	if st.Count() > 0 {
		return
	}

	now := time.Now()
	samples := []store.Message{
		{
			Sender:      "106939502038450",
			Body:        "【深圳一流网络技术】恭喜您成功注册成为一牛网会员，随机密码：06692310，请登录后修改密码！",
			Time:        now.Add(-time.Minute).Format("2006-01-02 15:04:05"),
			SortKey:     now.Add(-time.Minute).Unix(),
			Code:        "06692310",
			Tag:         "密码",
			ServiceName: "深圳一流网络技术",
			Raw:         "演示数据：+CMGR PDU 解码结果",
		},
		{
			Sender:  "10086",
			Body:    "【中国移动】您本月流量已使用 80%，请注意合理使用。",
			Time:    now.Add(-2 * time.Minute).Format("2006-01-02 15:04:05"),
			SortKey: now.Add(-2 * time.Minute).Unix(),
			Raw:     "演示数据：普通短信",
		},
	}

	for _, m := range samples {
		if _, _, err := st.Add(m); err != nil {
			logs.Error("[main.seedDemoMessages] 写入示例短信失败 sender=%s error=%v", m.Sender, err)
		}
	}
	logs.Info("[main.seedDemoMessages] 已写入示例短信 count=%d", len(samples))
}
