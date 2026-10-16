# Security policy

## Supported versions

Only the latest release receives fixes. Installed copies update themselves unless automatic checks are turned off in **Settings > Updates**.

## Reporting a vulnerability

Please do not open a public issue. Report it privately through [GitHub's private vulnerability reporting](https://github.com/bharry29/panelith/security/advisories/new), or by email to bharry29@gmail.com with "Panelith security" in the subject.

Include the Panelith version, what an attacker could do, and steps to reproduce. You can expect a reply within a week. Once a fix is released, you will be credited in the release notes unless you prefer not to be.

## How releases are protected

- The installer and app are Authenticode-signed. The signing certificate is published with each release as `Panelith-code-signing.cer`, and `SHA256SUMS.txt` lists the hash of every file.
- The update feed and every update are signed with an Ed25519 key that never leaves the maintainer's machine. The app refuses an update whose signature does not match the public key built into it, even if the download site were compromised.
- Release builds stop if any NuGet dependency, direct or transitive, has a known vulnerability, and the bundled tools are checked against pinned SHA-256 hashes.

## Scope

Panelith runs with administrator rights when you allow it, so it can read hardware sensors. Problems that let another program or user gain those rights through Panelith are in scope, as are problems in the update process, the installer, and how Panelith handles screen files and videos from other people.
