"""Minimal local MCP bridge for the connect-the-dots.biz SSH host.

The private key never leaves the workstation. The MCP process invokes the
pre-installed PuTTY plink client with the configured PPK file.
"""

from __future__ import annotations

import os
import subprocess
from pathlib import Path

from mcp.server.fastmcp import FastMCP


HOST = os.environ.get("CONNECT_DOTS_SSH_HOST", "connect-the-dots.biz")
USER = os.environ.get("CONNECT_DOTS_SSH_USER", "root")
KEY = Path(os.environ.get("CONNECT_DOTS_SSH_KEY", ""))
PLINK = os.environ.get("CONNECT_DOTS_PLINK", r"C:\Program Files\PuTTY\plink.exe")

mcp = FastMCP("connect-the-dots-ssh")


@mcp.tool()
def run_ssh_command(command: str, timeout_seconds: int = 120) -> str:
    """Run a non-interactive command on the configured host over certificate SSH."""
    if not command.strip():
        raise ValueError("command must not be empty")
    if not KEY.is_file():
        raise RuntimeError("Configured SSH key file does not exist")
    if not Path(PLINK).is_file():
        raise RuntimeError("Configured plink executable does not exist")
    if timeout_seconds < 1 or timeout_seconds > 600:
        raise ValueError("timeout_seconds must be between 1 and 600")

    result = subprocess.run(
        [PLINK, "-batch", "-no-antispoof", "-i", str(KEY), f"{USER}@{HOST}", command],
        capture_output=True,
        text=True,
        timeout=timeout_seconds,
        check=False,
    )
    output = result.stdout
    if result.stderr:
        output += ("\n" if output else "") + result.stderr
    if result.returncode:
        raise RuntimeError(f"Remote command exited with {result.returncode}:\n{output}")
    return output or "(command completed with no output)"


if __name__ == "__main__":
    mcp.run()
