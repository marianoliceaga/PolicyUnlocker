This is a Windows .NET 8 WinForms utility for inspecting and removing registry-based Group Policy settings.

What it does
•	Requires administrator privileges through UAC.
•	Scans the 64-bit registry in:
	HKEY_LOCAL_MACHINE\SOFTWARE\Policies
	HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies
	The equivalent locations under HKEY_CURRENT_USER
  
•	Displays each policy registry value, including:
	Registry hive
	Key path
	Value name
	Value type
	Stored data

•	Detects Microsoft Intune/MDM enrollment providers and warns that organizational policies may be reapplied.
•	Lets the user filter, select, and remove individual policy values.

•	Provides a “Remove all” operation that:
	Removes every listed policy value
	Deletes local Registry.pol files under System32\GroupPolicy
	Runs gpupdate /force
  
•	Before making changes, exports the policy registry keys to .reg backup files and copies existing Registry.pol files.

•	Can open:
	gpedit.msc
	The backup directory

The program is intended to reset locally stored Windows, browser, antivirus, VPN, or other software policies. It does not remove device management enrollment, domain membership, or organizational management, so company-enforced settings may return.
