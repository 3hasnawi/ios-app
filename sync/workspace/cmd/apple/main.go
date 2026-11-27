package main

// #include <stdlib.h>
import "C"
import (
	"payroll/workspace"
	"unsafe"
)

//export payroll_sync
func payroll_sync(input *C.char) *C.char {
	return C.CString(string(workspace.Call([]byte(C.GoString(input)))))
}

//export payroll_sync_free
func payroll_sync_free(p *C.char) { C.free(unsafe.Pointer(p)) }
func main()                       {}
