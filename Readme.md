# Remote SQL Backup Service
This is a .net windows service written in c# that automates backing up from remote database services.

## Goals
Allow for backing up of databases hosted on remote platforms you may not have control over.
In this case, I was hosting on SmarterASP.net and utilizing both Microsoft SQL Server and 
MySQL databases and wanted to make sure I had regular backups of my data away from the 
hosting provider in case anything goes sideways. There are numerous options with command
line tools to create backups from remote database sources, but they do nothing to help with
managing multiple backups, nameing the file based on the date and time, or compressing the
output. This service aims to automate all of that.

This service was a quick and fun side-project for me to solve these problems - not intended
to showcase the best programming practices.

## Features
* Automatic File Naming based on Date and Time.
* Automatic ZIP File Compression.
* Compares backup output to previous output and removes the data if they're the same.
* Automatic File Rotation 

## Configuration
The service is configured using the appsettings.json by specifying one or more BackupSettings.

Each BackupSetting consists of: 
<table>
	<thead>
		<tr>
			<th>Property</th>
			<th>Type</th>
			<th>Description</th>
		</tr>
	</thead>
	<tbody>
		<tr>
			<td>Name</td>
			<td>String</td>
			<td>The display name of the setting used for logging.</td>
		</tr>
		<tr>
			<td>Progam</td>
			<td>String</td>
			<td>The full path to the program to execute without any arguments.</td>
		</tr>
		<tr>
			<td>ProgramParameters</td>
			<td>String</td>
			<td>
				<p>All the command line arguments to pass to the program to execute.</p>
				<p>Use <code>%filename%</code> to specify where the full path and file name should be entered.</p>
				<p>Keep in mind that full paths that include spaces should be quoted.</p>
			</td>
		</tr>
		<tr>
			<td>FrequencyTimeSpan</td>
			<td>TimeSpan</td>
			<td>
				<p>How often to run the backup expressed as a TimeSpan.</p>
				<p>Values greater than 365 days or less than 30 minutes are not supported.</p>
				<p>If the frequency is less than 24 hours, the backup will reset every day starting at 
				<code>TimeOfDay</code>.</p>
				<p>If the frequency is exactly 24 hours, the backup will run every day at <code>TimeOfDay</code></p>
				<p>If the frequency is less than or equal to 15 days, the backup will run offset from the first day of the month.</p>
				<p>Any frequency greater than 15 days will run offset from the first day of the year.</p>
				<p><strong>Examples:</strong></p>
				<ul>
					<li><code>1.0:00</code> 1 Day (24 hours)</li>
					<li><code>1:00</code> 1 Hour</li>
					<li><code>5.2:30</code> 5 Days, 2 hours and 30 minutes.</li>
				</ul>
			</td>
		</tr>
		<tr>
			<td>TimeOfDay</td>
			<td>TimeOnly</td>
			<td>
				<p>The time of day to start the backups - this is essentially an offset so backups don't all start at exacly midnight.</p>
				<p>The default value is <code>12:00 AM</code> which means the backup will start at midnight.</p
			</td>
		</tr>
		<tr>
			<td>KeepMostRecents</td>
			<td>Integer</td>
			<td>The number of most-recent backups to keep.</td>
		</tr>
		<tr>
			<td>KeepDays</td>
			<td>Integer</td>
			<td>The number of daily backups to keep - where only the first backup of the day is kept.</td>
		</tr>
		<tr>
			<td>KeepWeeks</td>
			<td>Integer</td>
			<td>The number of weekly backups to maintain - where only the earliest backup starting from Sunday is kept.</td>
		</tr>
		<tr>
			<td>KeepMonths</td>
			<td>Integer</td>
			<td>The number of single montly backups to keep. Only the first backup of the month is kept.</td>
		</tr>
		<tr>
			<td>BackupFileExtension</td>
			<td>String</td>
			<td>The extension of the backup file.</td>
		</tr>
		<tr>
			<td>BackupFolder</td>
			<td>String</td>
			<td>The folder to keep all the backups in.</td>
		</tr>
		<tr>
			<td>ZIPResults</td>
			<td>Boolean</td>
			<td>If true, the resulting file after the command is run will be zipped up.</td>
		</tr>
		<tr>
			<td>IsZIP</td>
			<td>Boolean</td>
			<td>Used to indicate the output of the command is actually a zip file regardless of extension. Microsoft .bacpac files are ZIP files in discuise for example.</td>
		</tr>
	</tbody>
</table>

## Output
The backup service runs the commands after replacing the `%filename%` placholder with a date and time name and extension. After the backup
successfully completes, the file is compared to the previous backup file and if they're the same, the file is deleted and replaced with a placeholder empty file with `.SameAsPrevious` 
extension before the expected normal file extension. If the file is different, the file 
is kept and optionally zipped up. The service will then analize all files in the 
directory and remove any outside the scope of Keep values specified in the configuration, ensuring
that files with actual data are maintained for any empty `.SameAsPrevious` files.

## Program Execution Notes
The backup service will execute any program you want with whatever program parameters are needed. Care was taken
with the example MSSQL and MySQL commands to ensure that the output is exactly the same on different
runs so that the service can determine if the backup is the same as the previous backup. A common
issue with backup programs is appending current dates or using unique GUIDs in the output, so that was 
avoided. HowToBackup.txt and HowToRestore.txt include some pointers on the configurations for those two
backup methods included in the AppSettings.json file.

## Installation
It wasn't a priority to create a windows installer for the service as it's more of a one-off, so
here's command-line instructions.
1. Build or download the latest release.
2. Update the appsettings.json file with the appropiate 
configurations for the databases you want backed up.
3. Register and start the service with the following administrator commands replacing 
the folder location with the location of the RemoteSQLBackup.exe file:

```
sc.exe create "Remote SQL Backup" binpath="C:\Program Files\RemoteSQLBackup\RemoteSQLBackup.exe"`

sc.exe description "Remote SQL Backup" "Runs backups on remote SQL databases using the specified commands found in its config file."`

sc config "Remote SQL Backup" start=delayed-auto`

sc start "Remote SQL Backup"`
```

## Development Note
https://github.com/dotnet/runtime/issues/97558 means that the published file cannot
be trimmed at this time as collections aren't being code generated correctly for configuration loading.