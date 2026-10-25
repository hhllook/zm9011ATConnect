include $(TOPDIR)/rules.mk

PKG_NAME:=zm9011d
PKG_VERSION:=0.1.0
PKG_RELEASE:=1
PKG_LICENSE:=Proprietary

include $(INCLUDE_DIR)/package.mk

define Package/zm9011d
  SECTION:=utils
  CATEGORY:=Utilities
  TITLE:=ZM9011 AT/SMS daemon with embedded web UI
  DEPENDS:=
endef

define Package/zm9011d/description
  ZM9011 模组 AT 调试与实时短信服务，内嵌 Web 界面。
  包内使用预编译的 aarch64（Cortex-A53）二进制；
  如果目标架构不同，请替换 files/zm9011d 后重新编译。
endef

define Package/zm9011d/conffiles
/etc/config/zm9011
endef

define Package/zm9011d/postinst
#!/bin/sh
[ -n "$${IPKG_INSTROOT}" ] || {
	mkdir -p /etc/zm9011
	/etc/init.d/zm9011 enable
	/etc/init.d/zm9011 restart
}
exit 0
endef

define Package/zm9011d/prerm
#!/bin/sh
[ -n "$${IPKG_INSTROOT}" ] || {
	/etc/init.d/zm9011 stop
	/etc/init.d/zm9011 disable
}
exit 0
endef

define Build/Prepare
	mkdir -p $(PKG_BUILD_DIR)
endef

define Build/Compile
endef

define Package/zm9011d/install
	$(INSTALL_DIR) $(1)/usr/bin
	$(INSTALL_BIN) $(CURDIR)/files/zm9011d $(1)/usr/bin/zm9011d
	$(INSTALL_DIR) $(1)/etc/config
	$(INSTALL_CONF) $(CURDIR)/files/zm9011.config $(1)/etc/config/zm9011
	$(INSTALL_DIR) $(1)/etc/init.d
	$(INSTALL_BIN) $(CURDIR)/files/zm9011.init $(1)/etc/init.d/zm9011
	$(INSTALL_DIR) $(1)/etc/zm9011
endef

$(eval $(call BuildPackage,zm9011d))
