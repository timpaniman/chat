$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$public = Join-Path $root "public"
$source = Join-Path $root "server.cs"

if (-not ("ChatRoomServer" -as [type])) {
  Add-Type -Path $source -ReferencedAssemblies "System.Web.Extensions"
}

[ChatRoomServer]::Run($public, 3000)
