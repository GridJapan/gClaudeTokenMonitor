//go:build windows

package main

import (
	"os"
	"syscall"
	"unsafe"
)

const (
	enableProcessedInput          = 0x0001
	enableLineInput               = 0x0002
	enableEchoInput               = 0x0004
	enableVirtualTerminalProcess  = 0x0004
	enableVirtualTerminalInputBit = 0x0200
)

var (
	kernel32                       = syscall.NewLazyDLL("kernel32.dll")
	procGetConsoleMode             = kernel32.NewProc("GetConsoleMode")
	procSetConsoleMode             = kernel32.NewProc("SetConsoleMode")
	procGetConsoleScreenBufferInfo = kernel32.NewProc("GetConsoleScreenBufferInfo")
)

type coord struct{ X, Y int16 }
type smallRect struct{ Left, Top, Right, Bottom int16 }
type screenBufferInfo struct {
	Size              coord
	CursorPosition    coord
	Attributes        uint16
	Window            smallRect
	MaximumWindowSize coord
}

// console holds the original modes so they can be restored on exit.
type console struct {
	inHandle, outHandle syscall.Handle
	inMode, outMode     uint32
	inOK, outOK         bool
}

func getMode(h syscall.Handle) (uint32, bool) {
	var m uint32
	r, _, _ := procGetConsoleMode.Call(uintptr(h), uintptr(unsafe.Pointer(&m)))
	return m, r != 0
}

func setMode(h syscall.Handle, m uint32) {
	procSetConsoleMode.Call(uintptr(h), uintptr(m))
}

// setupConsole enables ANSI output and single-keystroke input.
// ENABLE_PROCESSED_INPUT is deliberately left on so Ctrl+C still works.
func setupConsole() *console {
	c := &console{
		inHandle:  syscall.Handle(os.Stdin.Fd()),
		outHandle: syscall.Handle(os.Stdout.Fd()),
	}
	if m, ok := getMode(c.outHandle); ok {
		c.outMode, c.outOK = m, true
		setMode(c.outHandle, m|enableVirtualTerminalProcess)
	}
	if m, ok := getMode(c.inHandle); ok {
		c.inMode, c.inOK = m, true
		setMode(c.inHandle, m&^(enableLineInput|enableEchoInput))
	}
	return c
}

func (c *console) restore() {
	if c == nil {
		return
	}
	if c.inOK {
		setMode(c.inHandle, c.inMode)
	}
	if c.outOK {
		setMode(c.outHandle, c.outMode)
	}
}

// termSize returns the visible console width and height.
func termSize() (int, int) {
	var info screenBufferInfo
	r, _, _ := procGetConsoleScreenBufferInfo.Call(
		uintptr(syscall.Handle(os.Stdout.Fd())), uintptr(unsafe.Pointer(&info)))
	if r == 0 {
		return 100, 40
	}
	w := int(info.Window.Right-info.Window.Left) + 1
	h := int(info.Window.Bottom-info.Window.Top) + 1
	if w < 20 {
		w = 100
	}
	if h < 10 {
		h = 40
	}
	return w, h
}
