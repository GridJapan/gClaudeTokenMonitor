//go:build !windows

package main

import (
	"os"
	"strconv"
	"syscall"
)

// pidAlive reports whether a process with this id exists.
func pidAlive(pid int) bool {
	if pid <= 0 {
		return false
	}
	err := syscall.Kill(pid, 0)
	return err == nil || err == syscall.EPERM
}

// console is a no-op outside Windows: most POSIX terminals already speak ANSI.
// Keystrokes are read line-buffered there (press Enter after a key).
type console struct{}

func setupConsole() *console { return &console{} }
func (c *console) restore()  {}

func termSize() (int, int) {
	w, h := 100, 40
	if v, err := strconv.Atoi(os.Getenv("COLUMNS")); err == nil && v > 20 {
		w = v
	}
	if v, err := strconv.Atoi(os.Getenv("LINES")); err == nil && v > 10 {
		h = v
	}
	return w, h
}
