package config

import (
	"strings"
	"testing"
)

func TestParseUCI(t *testing.T) {
	text := `
config zm9011 'main'
    option device '/dev/ttyUSB2'
    option baud '921600'
    option listen '192.168.1.1'
    option port '8090'
    option password 'secret'
    option delete_after_read '0'
    option data_dir '/mnt/usb/zm9011'
`
	c, err := Parse(strings.NewReader(text), Default())
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if c.Device != "/dev/ttyUSB2" || c.Baud != 921600 || c.Listen != "192.168.1.1" ||
		c.Port != 8090 || c.Password != "secret" || c.DeleteAfterRead || c.DataDir != "/mnt/usb/zm9011" {
		t.Fatalf("unexpected config: %+v", c)
	}
}

func TestParseDefaults(t *testing.T) {
	c, err := Parse(strings.NewReader(""), Default())
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if c.Device != "auto" || c.Baud != 115200 || c.Port != 8080 || !c.DeleteAfterRead {
		t.Fatalf("unexpected default: %+v", c)
	}
}
