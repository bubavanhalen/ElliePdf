# Offline network-observation procedure

Use a clean Windows VM with Windows Firewall logging or an approved packet capture tool. Disconnect the VM from the
network (or deny all outbound traffic), install the signed package, and exercise launch, open, render, search, forms,
print, save, settings, and crash-recovery flows using synthetic fixtures. Confirm no outbound connection is attempted.
Then repeat once with only an explicit update check or user-enabled diagnostics and record the destination category and
user action. Do not record document paths, filenames, extracted content, account identifiers, or packet payloads.

This is an operator gate, not a normal CI test. Attach the firewall/capture summary, OS build, package version, and
pass/fail result to the release record. Any unexpected network activity blocks promotion until explained and fixed.
