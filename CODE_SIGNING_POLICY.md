# Code signing policy

- **What is signed:** only `SensorBridge.exe` and `KeyHook.exe`, built from a tagged commit of this repository, without
  changes. Nothing else is signed with this certificate — in particular not the closed-source parts of Lazarus.
- **Who signs:** the maintainer, Alexander Polushkin, with a Certum Open Source Code Signing certificate (the signature
  names him as "Open Source Developer"). Each release is built and signed by him; there is no other signer.
- **Status:** the certificate is not in place yet — until it is, the binaries are unsigned. This file will say so when
  that changes.
- **How to check a binary:** Properties → Digital Signatures in Explorer, or
  `Get-AuthenticodeSignature .\SensorBridge.exe` in PowerShell. To see that the code matches this repository, build the
  tag yourself (see [README.md](README.md)) and compare what the programs do; the CI artifacts of each commit carry
  SHA-256 sums of the unsigned build.
- **Privacy:** the programs collect nothing about you and send nothing anywhere except what [README.md](README.md)
  lists (sensor values to `127.0.0.1`, a ping to `1.1.1.1`).
- **Reporting a problem:** open an issue in this repository.
