// mkpackage 生成 OpenWrt 安装包：
//   - ipk（opkg）
//   - tar.gz 通用离线安装包（apk/opkg 均可）
//
// 用法示例：
//
//	go run ./tools/mkpackage \
//	  -binary dist/zm9011d-linux-arm64 \
//	  -config ../files/etc/config/zm9011 \
//	  -init ../files/etc/init.d/zm9011 \
//	  -version 0.1.0 -arch aarch64_cortex-a53 -outdir dist
package main

import (
	"archive/tar"
	"bytes"
	"compress/gzip"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"
)

type entry struct {
	name  string
	mode  int64
	data  []byte
	isDir bool
}

func main() {
	binaryPath := flag.String("binary", "dist/zm9011d-linux-arm64", "arm64 二进制路径")
	configPath := flag.String("config", "../files/etc/config/zm9011", "UCI 配置路径")
	initPath := flag.String("init", "../files/etc/init.d/zm9011", "init 脚本路径")
	version := flag.String("version", "0.1.0", "版本号")
	arch := flag.String("arch", "aarch64_cortex-a53", "opkg 架构名")
	outDir := flag.String("outdir", "dist", "输出目录")
	verify := flag.Bool("verify", false, "输出包结构校验信息")
	flag.Parse()

	binary, err := os.ReadFile(*binaryPath)
	must(err)
	config, err := os.ReadFile(*configPath)
	must(err)
	initScript, err := os.ReadFile(*initPath)
	must(err)

	must(os.MkdirAll(*outDir, 0755))

	ipkPath := filepath.Join(*outDir, fmt.Sprintf("zm9011d_%s-1_%s.ipk", *version, *arch))
	must(buildIPK(ipkPath, binary, config, initScript, *version, *arch))
	fmt.Printf("ipk:   %s (%s)\n", ipkPath, humanSize(ipkPath))

	tarPath := filepath.Join(*outDir, fmt.Sprintf("zm9011d-%s-aarch64-openwrt.tar.gz", *version))
	must(buildTar(tarPath, binary, config, initScript))
	fmt.Printf("tar:   %s (%s)\n", tarPath, humanSize(tarPath))

	if *verify {
		fmt.Println("--- ipk 结构 ---")
		inspectIPK(ipkPath)
		fmt.Println("--- tar.gz 结构 ---")
		inspectTarGz(tarPath)
	}
}

func buildIPK(out string, binary, config, initScript []byte, version, arch string) error {
	control := fmt.Sprintf(`Package: zm9011d
Version: %s-1
Source: zm9011d
Section: utils
Priority: optional
Maintainer: ZM9011 <noreply@example.com>
Architecture: %s
Installed-Size: %d
Description: ZM9011 AT/SMS daemon with embedded web UI
`, version, arch, (len(binary)+len(config)+len(initScript))/1024+1)

	postinst := []byte(`#!/bin/sh
[ -n "$IPKG_INSTROOT" ] && exit 0
mkdir -p /etc/zm9011
/etc/init.d/zm9011 enable
/etc/init.d/zm9011 restart
exit 0
`)

	prerm := []byte(`#!/bin/sh
[ -n "$IPKG_INSTROOT" ] && exit 0
/etc/init.d/zm9011 stop
/etc/init.d/zm9011 disable
exit 0
`)

	controlEntries := []entry{
		{name: "./control", mode: 0644, data: []byte(control)},
		{name: "./conffiles", mode: 0644, data: []byte("/etc/config/zm9011\n")},
		{name: "./postinst", mode: 0755, data: postinst},
		{name: "./prerm", mode: 0755, data: prerm},
	}

	dataEntries := []entry{
		{name: "./usr/", mode: 0755, isDir: true},
		{name: "./usr/bin/", mode: 0755, isDir: true},
		{name: "./usr/bin/zm9011d", mode: 0755, data: binary},
		{name: "./etc/", mode: 0755, isDir: true},
		{name: "./etc/config/", mode: 0755, isDir: true},
		{name: "./etc/config/zm9011", mode: 0644, data: config},
		{name: "./etc/init.d/", mode: 0755, isDir: true},
		{name: "./etc/init.d/zm9011", mode: 0755, data: initScript},
		{name: "./etc/zm9011/", mode: 0755, isDir: true},
	}

	controlTar, err := tarGz(controlEntries)
	if err != nil {
		return err
	}
	dataTar, err := tarGz(dataEntries)
	if err != nil {
		return err
	}

	// 与 OpenWrt 官方 ipk 一致：外层是 gzip + tar，
	// 内含 ./debian-binary、./data.tar.gz、./control.tar.gz。
	// 注意：部分 opkg/libarchive 版本不接受传统 ar 容器，会报 Malformed package file。
	outer, err := tarGz([]entry{
		{name: "./debian-binary", mode: 0644, data: []byte("2.0\n")},
		{name: "./data.tar.gz", mode: 0644, data: dataTar},
		{name: "./control.tar.gz", mode: 0644, data: controlTar},
	})
	if err != nil {
		return err
	}
	return os.WriteFile(out, outer, 0644)
}

func buildTar(out string, binary, config, initScript []byte) error {
	install := []byte(`#!/bin/sh
set -eu

DIR=$(cd "$(dirname "$0")" && pwd)

[ "$(id -u)" = "0" ] || { echo "请用 root 运行：sh install.sh"; exit 1; }

mkdir -p /usr/bin /etc/config /etc/init.d /etc/zm9011
cp "$DIR/usr/bin/zm9011d" /usr/bin/zm9011d
chmod 755 /usr/bin/zm9011d

if [ ! -f /etc/config/zm9011 ]; then
	cp "$DIR/etc/config/zm9011" /etc/config/zm9011
fi

cp "$DIR/etc/init.d/zm9011" /etc/init.d/zm9011
chmod 755 /etc/init.d/zm9011

/etc/init.d/zm9011 enable
/etc/init.d/zm9011 restart

IP=$(ip route get 1 2>/dev/null | awk '{print $7; exit}')
echo "zm9011d 安装完成，访问：http://${IP:-<路由器IP>}:8080"
`)

	uninstall := []byte(`#!/bin/sh
set -eu

[ "$(id -u)" = "0" ] || { echo "请用 root 运行：sh uninstall.sh"; exit 1; }

/etc/init.d/zm9011 stop 2>/dev/null || true
/etc/init.d/zm9011 disable 2>/dev/null || true
rm -f /usr/bin/zm9011d /etc/init.d/zm9011

echo "zm9011d 已卸载。"
echo "配置 /etc/config/zm9011 和短信 /etc/zm9011 已保留；如需清理请手动删除。"
`)

	readme := []byte(`ZM9011 OpenWrt 安装包
=====================

安装：
  mkdir -p /tmp/zm9011d-install
  tar -xzf zm9011d-0.1.0-aarch64-openwrt.tar.gz -C /tmp/zm9011d-install
  sh /tmp/zm9011d-install/install.sh

访问：
  http://<路由器LAN地址>:8080

配置：
  /etc/config/zm9011
  默认 device=auto（自动探测 AT 口）；如与上网拨号冲突，请指定独立的 AT 口。

日志：
  logread -f | grep -i zm9011d

卸载：
  sh /tmp/zm9011d-install/uninstall.sh

更多说明见项目 OpenWrt/README.md。
`)

	entries := []entry{
		{name: "./install.sh", mode: 0755, data: install},
		{name: "./uninstall.sh", mode: 0755, data: uninstall},
		{name: "./README.txt", mode: 0644, data: readme},
		{name: "./usr/", mode: 0755, isDir: true},
		{name: "./usr/bin/", mode: 0755, isDir: true},
		{name: "./usr/bin/zm9011d", mode: 0755, data: binary},
		{name: "./etc/", mode: 0755, isDir: true},
		{name: "./etc/config/", mode: 0755, isDir: true},
		{name: "./etc/config/zm9011", mode: 0644, data: config},
		{name: "./etc/init.d/", mode: 0755, isDir: true},
		{name: "./etc/init.d/zm9011", mode: 0755, data: initScript},
	}

	data, err := tarGz(entries)
	if err != nil {
		return err
	}
	return os.WriteFile(out, data, 0644)
}

func tarGz(entries []entry) ([]byte, error) {
	var buf bytes.Buffer
	gz := gzip.NewWriter(&buf)
	tw := tar.NewWriter(gz)
	now := time.Now()

	for _, e := range entries {
		hdr := &tar.Header{
			Name:    e.name,
			Mode:    e.mode,
			ModTime: now,
			Uid:     0,
			Gid:     0,
		}
		if e.isDir {
			hdr.Typeflag = tar.TypeDir
			hdr.Size = 0
		} else {
			hdr.Typeflag = tar.TypeReg
			hdr.Size = int64(len(e.data))
		}
		if err := tw.WriteHeader(hdr); err != nil {
			return nil, err
		}
		if !e.isDir {
			if _, err := tw.Write(e.data); err != nil {
				return nil, err
			}
		}
	}

	if err := tw.Close(); err != nil {
		return nil, err
	}
	if err := gz.Close(); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

func humanSize(path string) string {
	info, err := os.Stat(path)
	if err != nil {
		return "?"
	}
	return fmt.Sprintf("%.1f MB", float64(info.Size())/1024/1024)
}

func inspectIPK(file string) {
	data, err := os.ReadFile(file)
	if err != nil {
		fmt.Println("读取失败:", err)
		return
	}

	gz, err := gzip.NewReader(bytes.NewReader(data))
	if err != nil {
		fmt.Println("不是 gzip 格式:", err)
		return
	}
	defer gz.Close()

	tr := tar.NewReader(gz)
	for {
		hdr, err := tr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			fmt.Println("外层 tar 解析失败:", err)
			return
		}

		kind := "file"
		if hdr.Typeflag == tar.TypeDir {
			kind = "dir "
		}
		fmt.Printf("  %s %-24s %d bytes\n", kind, hdr.Name, hdr.Size)

		if strings.HasSuffix(hdr.Name, ".tar.gz") {
			inner, err := io.ReadAll(tr)
			if err == nil {
				listTarGz(inner)
			}
		}
	}
}

func inspectTarGz(file string) {
	data, err := os.ReadFile(file)
	if err != nil {
		fmt.Println("读取失败:", err)
		return
	}
	listTarGz(data)
}

func listTarGz(data []byte) {
	gz, err := gzip.NewReader(bytes.NewReader(data))
	if err != nil {
		fmt.Println("    gzip 解析失败:", err)
		return
	}
	defer gz.Close()

	tr := tar.NewReader(gz)
	for {
		hdr, err := tr.Next()
		if err == io.EOF {
			break
		}
		if err != nil {
			fmt.Println("    tar 解析失败:", err)
			return
		}
		kind := "file"
		if hdr.Typeflag == tar.TypeDir {
			kind = "dir "
		}
		fmt.Printf("    %s %-28s %8d  %o\n", kind, hdr.Name, hdr.Size, hdr.Mode)
	}
}

func must(err error) {
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
