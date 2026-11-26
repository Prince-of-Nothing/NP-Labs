//go:build !windows

package main

import "syscall"

func setReuse(fd uintptr) error {
	f := int(fd)
	if err := syscall.SetsockoptInt(f, syscall.SOL_SOCKET, syscall.SO_REUSEADDR, 1); err != nil {
		return err
	}
	_ = syscall.SetsockoptInt(f, syscall.SOL_SOCKET, syscall.SO_BROADCAST, 1)
	_ = syscall.SetsockoptInt(f, syscall.SOL_SOCKET, 15, 1)
	return nil
}
