Build inputs for the native Nvpwr executable.

Required embedded components:
  Nvpwr.sys               Existing local power-policy kernel driver
  NvpwrCtl.exe            Existing local command-line power controller
  KDU.exe                 Existing KDU 1.5.0.2607 executable
  drv64.dll               Matching KDU provider database
  EfiDSEFix.exe           Existing EFI runtime helper
  bootx64.efi             Existing EfiGuard boot loader
  EfiGuardDxe.efi          Existing EfiGuard DXE driver

These binaries are not rebuilt or modified by this project. They are required
build inputs referenced by Nvpwr.csproj, not disposable build outputs. They are
embedded in release/Nvpwr.exe. The application extracts needed components to
NvpwrData beside its executable and checks them against embedded resources
before reuse. It does not use LocalAppData as a runtime cache.

EFI boot files can be exported explicitly from the application. The application
does not mount an EFI system partition, install a boot entry, or reboot Windows.

Upstream projects and their licenses still apply:
https://github.com/LevinAi-arch/rtx-5070ti-laptop-160w-power-limit
https://github.com/hfiref0x/KDU
https://github.com/Mattiwatti/EfiGuard

Upstream license notices are preserved alongside these build inputs:
  LICENSE.KDU.txt         KDU Project, MIT License
  LICENSE.EfiGuard.txt    EfiGuard, GNU General Public License version 3

These license texts were checked against the upstream Git blobs on 2026-09-26.
The original power-control repository does not declare a license. Its public
availability does not establish permission to redistribute or modify it.
This project does not grant additional rights to any third-party component.

The bundled binaries come from the existing local project, not a newly rebuilt
or independently certified upstream distribution. Upstream links do not prove
that a particular revision is the corresponding source for these exact files.
Anyone redistributing them must satisfy the applicable license and source
requirements, including requirements for any local modifications.