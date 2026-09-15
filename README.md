# Navlan - Secure LAN File Transfer

Navlan is a Windows WPF desktop application for encrypted peer-to-peer file transfers over a local area network (LAN).

## Requirements
- Windows 10 1809+ (x64)
- .NET 10 Windows Desktop Runtime
- .NET 10 SDK (for building)

## Building

dotnet build WpfApp3.csproj

## Release Build

dotnet build WpfApp3.csproj -c Release

## Publish

dotnet publish WpfApp3.csproj -c Release -r win-x64 --self-contained false -o bin\publish

## Running

dotnet run --project WpfApp3.csproj

## Network Ports
- TCP 42000: Pairing control channel
- TCP 42001: Secure TLS file transfer
- UDP 42101: Peer discovery (broadcast)

## Data Storage
All data is stored in %LOCALAPPDATA%\LANShare\
- device.pfx: Self-signed TLS device certificate
- paired-devices.json: Trusted device registry

## Security
- TLS 1.3/1.2 encrypted transport
- Mutual X.509 certificate pinning
- SHA-256 file integrity verification
- RSA digital signature pairing challenge

## Troubleshooting
- Devices not discovered: ensure same subnet, open UDP 42101 in Windows Firewall
- Transfer fails: open TCP 42000 and 42001 in Windows Firewall, ensure devices are paired
- Certificate errors: delete %LOCALAPPDATA%\LANShare\device.pfx to regenerate
