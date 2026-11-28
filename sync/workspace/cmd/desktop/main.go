package main

import (
	"fmt"
	"io"
	"os"
	"payroll/workspace"
)

func main() {
	data, e := io.ReadAll(io.LimitReader(os.Stdin, 32<<20))
	if e != nil {
		os.Exit(1)
	}
	var onTransfer func(bool)
	if len(os.Args) == 2 && os.Args[1] == "--transfer-events" {
		onTransfer = func(active bool) { fmt.Fprintln(os.Stderr, "transfer", active) }
	}
	os.Stdout.Write(workspace.CallWithTransfer(data, onTransfer))
}
