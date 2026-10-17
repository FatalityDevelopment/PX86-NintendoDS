#!/usr/bin/env bash
# SessionStart hook: make sure the .NET 8 SDK is available and packages are
# restored, so `dotnet build` / `dotnet test` work from the first prompt.
#
# On a developer machine with dotnet already installed this only restores.
# In a Claude Code cloud session (CLAUDE_CODE_REMOTE=true) without dotnet it
# installs the SDK into ~/.dotnet with Microsoft's dotnet-install.sh.
set -euo pipefail

cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}"

if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export PATH="$HOME/.dotnet:$PATH"
fi

if ! command -v dotnet >/dev/null 2>&1; then
  if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
    echo "dotnet not found. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0" >&2
    exit 0
  fi
  echo "Installing .NET 8 SDK into \$HOME/.dotnet ..." >&2
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet" >/dev/null
  export PATH="$HOME/.dotnet:$PATH"
fi

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export PATH=\"$HOME/.dotnet:\$PATH\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
  } >> "$CLAUDE_ENV_FILE"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet restore Px86.NintendoDS.sln >/dev/null
echo "Px86.NintendoDS: .NET $(dotnet --version) ready, packages restored." >&2
