//go:build !windows

package dat2img

import "os/exec"

func prepareCommand(cmd *exec.Cmd) {}
