using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Numerics;
using System.Reflection.Metadata.Ecma335;

namespace RemoteSQLBackup;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> logger;
    private BackupSetting[] backupSettings;
    private Dictionary<int, Timer> timers = [];
    private Dictionary<int, DateTime> futureBackupInfo = [];
    private List<int> runningBackups = [];
    private bool isCancelled = false;

    const string fileDateNameFormat = "yyyyMMdd_HHmm";
    const string sameAsPrviousIndicator = ".SameAsPrevious";
    const string zipFileExtension = ".zip";

    public Worker(ILogger<Worker> logger, IConfiguration configuration)
    {
        this.logger = logger;

        backupSettings = configuration.GetSection("BackupSettings").Get<BackupSetting[]>() ?? [];
        // Give the backup settings a unique ID.
        for (var i = 0; i < backupSettings.Length; i++)
            backupSettings[i].Id = i + 1;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (backupSettings.Length == 0)
        {
            logger.LogWarning("No backup settings found in configuration, nothing to backup.");
            logger.LogInformation("Current Working Directory is: {WorkingDirectory}", Environment.CurrentDirectory);
        }

        isCancelled = false;
        try
        {
            lock (timers)
            {
                // Using the current time, create a timer for each backup.
                var currentTime = DateTime.Now;
                foreach (var backupSetting in backupSettings)
                {
                    futureBackupInfo[backupSetting.Id] = backupSetting.GetNextRunTime(currentTime);
                    logger.LogInformation("Scheduling \"{Name}\" to run at {Time}", backupSetting.Name, futureBackupInfo[backupSetting.Id]);
                    timers[backupSetting.Id] = new Timer(RunBackup, backupSetting.Id, futureBackupInfo[backupSetting.Id] > DateTime.Now ? futureBackupInfo[backupSetting.Id].Subtract(DateTime.Now) : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(900000, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // this is expected.
        }
        catch (Exception ex)
        {
            // In order for the Windows Service Management system to leverage configured
            // recovery options, we need to terminate the process with a non-zero exit code.
            logger.LogError(ex, "An unexpected error occred while starting up the backup process.");
            Environment.Exit(1);
        }
        finally
        {
            // Indicate to child threads that we're done.
            isCancelled = true;

            // Kill all future timers.
            lock (timers)
            {
                while (timers.Count > 0)
                {
                    var key = timers.Keys.First();
                    var timer = timers[key];
                    timers[key].Change(Timeout.Infinite, Timeout.Infinite);
                    timers.Remove(key);
                    timer.Dispose();
                }
            }

            // Wait for all running backups to complete.
            while (runningBackups.Count > 0)
                await Task.Delay(250); // Quarter second
        }
    }

    private void RunBackup(object? backupSettingsId)
    {

        int id = (int?)backupSettingsId ?? 0;
        var backupSetting = backupSettings.Where(x => x.Id == id).FirstOrDefault();
        if (backupSetting == default)
            return;

        runningBackups.Add(id);

        var fileName = $"{futureBackupInfo[id].ToString(fileDateNameFormat)}{(backupSetting.BackupFileExtension.Trim().StartsWith('.') ? "" : ".")}{backupSetting.BackupFileExtension.Trim()}";
        var fileNameWithFolder = Path.Combine(backupSetting.BackupFolder, fileName);

        // Make sure folder exists.
        var diFolder = new DirectoryInfo(backupSetting.BackupFolder);
        if (!diFolder.Exists)
            diFolder.Create();

        var psi = new ProcessStartInfo(backupSetting.Program, backupSetting.ProgramParameters?.Replace("%filename%", fileNameWithFolder, StringComparison.OrdinalIgnoreCase) ?? string.Empty)
        { CreateNoWindow = true, UseShellExecute = false, LoadUserProfile = true };

        Process? p = null;
        try
        {
            p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "The backup for \"{Name}\" failed to start.", backupSetting.Name);
        }

        var commandSucceeded = false;
        if (p != null)
        {
            p.WaitForExit();

            // See if any failed.
            if (p.ExitCode != 0)
                logger?.LogError("The backup for \"{Name}\" failed with exit code {ExitCode}.", backupSetting.Name, p.ExitCode);
            else
                commandSucceeded = true;

            try
            {
                // Log that it completed and close the process.
                logger?.LogInformation("Completed backup for \"{Name}\" in {TimeTaken} to {FileName} with Exit Code {ExitCode}.",
                    backupSetting.Name, p.StartTime.Subtract(p.ExitTime), fileName, p.ExitCode);
                p.Close();
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "There was a problem reading process end information for \"{Name}\".", backupSetting.Name);
            }
            finally
            {
                p.Dispose();
            }
        }

        if (!commandSucceeded)
        {
            // Remove the file if it was created.
            if (Path.Exists(fileNameWithFolder)) File.Delete(fileNameWithFolder);
        }

        // Cleanup any old files and check if we're the same.
        if (!isCancelled && commandSucceeded)
            Cleanup(backupSetting, fileNameWithFolder);

        // ZIP up the file.
        if (!isCancelled 
            && commandSucceeded 
            && backupSetting.ZIPResults 
            && !backupSetting.BackupFileExtension.EndsWith(zipFileExtension) 
            && Path.Exists(fileNameWithFolder)) // It may have been turned into .SameAsPrevious.zip 0 length file by Cleanup
        {
            using (var zipArchive = ZipFile.Open(Path.Combine(backupSetting.BackupFolder, Path.GetFileNameWithoutExtension(fileNameWithFolder) + zipFileExtension), ZipArchiveMode.Create))
                zipArchive.CreateEntryFromFile(fileNameWithFolder, fileName, CompressionLevel.SmallestSize);

            // Delete the original file.
            File.Delete(fileNameWithFolder);
            fileNameWithFolder = Path.Combine(backupSetting.BackupFolder, Path.GetFileNameWithoutExtension(fileNameWithFolder) + zipFileExtension);
            fileName = Path.GetFileName(fileNameWithFolder);
        }

        lock (timers)
        {
            // Reset the  timer for next schedule backup.
            if (!isCancelled && timers.TryGetValue(id, out var timer))
            {
                futureBackupInfo[id] = backupSetting.GetNextRunTime(DateTime.Now);
                timer.Change(futureBackupInfo[id] > DateTime.Now ? futureBackupInfo[id].Subtract(DateTime.Now) : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        // Remove ourselves from running status.
        runningBackups.Remove(id);
    }

    private void Cleanup(BackupSetting backupSetting, string currentFullFileName)
    {
        // Get a list of all backup files in the backup folder.
        var existingBackupFiles = new DirectoryInfo(backupSetting.BackupFolder).GetFiles()
            .Where(x =>
                x.Name.IndexOf('.') == fileDateNameFormat.Length
                && DateTime.TryParseExact(x.Name[..fileDateNameFormat.Length], fileDateNameFormat, null, System.Globalization.DateTimeStyles.None, out _)
            )
            .Select(x => new BackupFileInfo
            {
                FileName = x.Name,
                SameAsPrevious = x.Name.IndexOf($"{sameAsPrviousIndicator}") > -1,
                IsZip = backupSetting.IsZIP || x.Name.EndsWith(zipFileExtension), // .bacpac files are zip files with different extension, so specified in the appsettings.
                BackupDate = DateTime.ParseExact(x.Name[..fileDateNameFormat.Length], fileDateNameFormat, null),
                Keep = false
            })
            .ToList();
        var curBackup = existingBackupFiles.OrderByDescending(x => x.BackupDate).FirstOrDefault();
        var prevBackup = existingBackupFiles.OrderByDescending(x => x.BackupDate).Where(x => !x.SameAsPrevious).Skip(1).FirstOrDefault();
        if (curBackup != default && prevBackup != default)
        {
            if (AreFilesSame(Path.Combine(backupSetting.BackupFolder, curBackup.FileName), curBackup.IsZip,
                Path.Combine(backupSetting.BackupFolder, prevBackup.FileName), prevBackup.IsZip))
            {
                File.Delete(Path.Combine(backupSetting.BackupFolder, curBackup.FileName));

                curBackup.FileName = Path.GetFileNameWithoutExtension(curBackup.FileName) + sameAsPrviousIndicator + (backupSetting.ZIPResults ? zipFileExtension : Path.GetExtension(curBackup.FileName));

                // Create the placeholder file.
                File.Create(Path.Combine(backupSetting.BackupFolder, curBackup.FileName)).Dispose();

                // Set it's SameAsPrevious property to true.
                curBackup.SameAsPrevious = true;
            }
        }

        // Using the backup settings, mark each file to keep or not.
        // We ALWAYS keep the most recent backup...
        if (curBackup != default) curBackup.Keep = true;
        // Don't worry about checking if SameAsPrvious on first pass, just locate the root files.
        if (backupSetting.KeepMostRecents > 0)
        {
            foreach (var bi in existingBackupFiles.OrderByDescending(x => x.BackupDate).Take(backupSetting.KeepMostRecents))
                bi.Keep = true;
        }
        var curDay = curBackup != default ? curBackup.BackupDate.Date : DateTime.Now.Date;
        if (backupSetting.KeepDays > 0)
        {
            for (var i = 0; i < backupSetting.KeepDays; i++)
            {
                var firstInDay = existingBackupFiles
                    .Where(x => x.BackupDate >= curDay.AddDays(-1 * i) && x.BackupDate < curDay.AddDays(-1 * (i - 1)))
                    .OrderBy(x => x.BackupDate)
                    .FirstOrDefault();
                if (firstInDay != default) firstInDay.Keep = true;
            }
        }
        if (backupSetting.KeepWeeks > 0)
        {
            // Get most recent sunday.
            var startOfWeek = curDay.AddDays(-1 * (int)curDay.DayOfWeek);
            for (var i = 0; i < backupSetting.KeepWeeks; i++)
            {
                var firstInWeek = existingBackupFiles
                    .Where(x => x.BackupDate >= startOfWeek.AddDays(-1 * i * 7) && x.BackupDate < startOfWeek.AddDays(-1 * (i - 1) * 7))
                    .OrderBy(x => x.BackupDate)
                    .FirstOrDefault();
                if (firstInWeek != default) firstInWeek.Keep = true;
            }
        }
        if (backupSetting.KeepMonths > 0)
        {
            var startOfMonth = new DateTime(curDay.Year, curDay.Month, 1);
            for (var i = 0; i < backupSetting.KeepMonths; i++)
            {
                var firstInMonth = existingBackupFiles
                    .Where(x => x.BackupDate >= startOfMonth.AddMonths(-1 * i) && x.BackupDate < startOfMonth.AddMonths(-1 * (i - 1)))
                    .OrderBy(x => x.BackupDate)
                    .FirstOrDefault();
                if (firstInMonth != default) firstInMonth.Keep = true;
            }
        }

        // OK, now that we marked all the ones we need to keep, if any are SameAsPrevious, make sure to keep the prvious backup that's not
        // SameAsPrevious
        var needsPrviousBackupList = existingBackupFiles.Where(x => x.Keep && x.SameAsPrevious).ToList();
        foreach (var bi in needsPrviousBackupList)
        {
            var prev = existingBackupFiles.Where(x => x.BackupDate < bi.BackupDate && !x.SameAsPrevious).OrderByDescending(x => x.BackupDate).FirstOrDefault();
            if (prev != default)
                prev.Keep = true;
        }

        // Now delete any that we no longer need.
        foreach (var bi in existingBackupFiles.Where(x => !x.Keep))
            File.Delete(Path.Combine(backupSetting.BackupFolder, bi.FileName));
    }

    /// <summary>
    /// Compares two files to see if they contain the same data, taking into account if they're ZIP file format or not.
    /// </summary>
    /// <param name="fullFile1Name">The full path and file name of the first file.</param>
    /// <param name="is1ZipFile">If true, indcates the first file should be treated as a ZIP archive.</param>
    /// <param name="fullFile2Name">The full path and file name of the second file.</param>
    /// <param name="is2ZipFile">If true, indicates the second file should be treated as a ZIP archive.</param>
    /// <returns>True if the files contain the same data, else false.</returns>
    private bool AreFilesSame(string fullFile1Name, bool is1ZipFile, string fullFile2Name, bool is2ZipFile)
    {
        var fi1 = new FileInfo(fullFile1Name);
        var fi2 = new FileInfo(fullFile2Name);

        // If neither one is a zip file and the length is different, then they're different.
        if (!is1ZipFile && !is2ZipFile && fi1.Length != fi2.Length)
            return false;

        // If one is a zip file and another isn't then we gotta examine the contents of one zip against the other unzipped.
        if (is1ZipFile != is2ZipFile)
        {
            var unzippedFileInfo = is1ZipFile ? fi2 : fi1;
            using var zipFile = ZipFile.OpenRead(is1ZipFile ? fullFile1Name : fullFile2Name);

            // Easy check, if zip file doesn't contain exactly one entry then it cannot be the same.
            if (zipFile.Entries.Count != 1)
                return false;

            // If the length of the two files doesn't match, then they're different.
            if (zipFile.Entries[0].Length != unzippedFileInfo.Length)
                return false;

            // Compare the file bytes
            using (Stream ms1 = (Stream)unzippedFileInfo.OpenRead() ?? new MemoryStream(0), ms2 = zipFile.Entries[0].Open())
                try
                {
                    return CompareBytes(ms1, ms2, unzippedFileInfo.Length);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error comparing two zip files, \"{File1FullName}\" and \"{File2FullName}\"",
                        fullFile1Name, fullFile2Name);
                    return false;
                }
            
        }
        else if (is1ZipFile && is2ZipFile)
        {
            using var zip1 = ZipFile.OpenRead(fullFile1Name);
            using var zip2 = ZipFile.OpenRead(fullFile2Name);
            // there must be the same number of entries (zipped content)
            if (zip1.Entries.Count != zip2.Entries.Count)
                return false;

            // Special case for archives with single file - the name doesn't matter (it's datestamped name)
            if (zip1.Entries.Count == 1)
            {
                if (zip1.Entries[0].Length != zip2.Entries[0].Length)
                    return false;

                using (Stream ms1 = zip1.Entries[0].Open(), ms2 = zip2.Entries[0].Open())
                    try
                    {

                        return CompareBytes(ms1, ms2, zip1.Entries[0].Length);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error comparing two zip files \"{File1FullName}\" and \"{File2FullName}\"",
                            fullFile1Name, fullFile2Name);
                        return false;
                    }
            }
            else
            {
                // Compare each file by name.
                foreach (var entry1 in zip1.Entries)
                {
                    var entry2 = zip2.GetEntry(entry1.FullName);
                    if (entry2 == default)
                        return false;
                    if (entry1.Length != entry2.Length)
                        return false;
                    using (Stream ms1 = entry1.Open(), ms2 = entry2.Open())
                        try
                        {
                            if (!CompareBytes(ms1, ms2, entry1.Length))
                                return false;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error comparing two zip files, \"{File1FullName}\" and \"{File2FullName}\" entries \"{Entry1Name}\" and \"{Entry2Name}\"",
                                fullFile1Name, fullFile2Name, entry1.Name, entry2.Name);
                            return false;
                        }

                }
            }

            // If made it here, we're good!
            return true;
        }
        else
        {
            // We already checked above if file lengths were the same.
            // Neither one is ZIP file, compare bytes.
            using (Stream ms1 = (Stream)fi1.OpenRead() ?? new MemoryStream(0), ms2 = (Stream)fi2.OpenRead() ?? new MemoryStream(0))
                try
                {
                    return CompareBytes(ms1, ms2, fi1.Length);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error comparing two files \"{File1FullName}\" and \"{File2FullName}\"",
                        fullFile1Name, fullFile2Name);
                    return false;
                }
        }

        return false;
    }

    /// <summary>
    /// Compares the bytes of two streams.
    /// </summary>
    /// <param name="source1">The first stream to compare.</param>
    /// <param name="source2">The second stream to compare.</param>
    /// <returns>True if all bytes are the same, else false.</returns>
    /// <remarks>This function expects that both stream lengths are exactly the same.</remarks>
    private bool CompareBytes(Stream source1, Stream source2, long sourceLength)
    {
        var ba1 = new byte[Math.Min(sourceLength, 1024 * 1024)];
        var ba2 = new byte[Math.Min(sourceLength, 1024 * 1024)];

        // Using Span of byte, compare the contents of the two streams
        int b1ReadLength = 0;
        int vectorSize = Vector<byte>.Count;

        b1ReadLength = source1.Read(ba1);

        while (b1ReadLength > 0)
        {

            source2.ReadExactly(ba2, 0, b1ReadLength);
            int position = 0;

            // Process in chunks using vectorized operations
            for (; position <= b1ReadLength - vectorSize; position += vectorSize)
            {
                if (!Vector.EqualsAll(
                    new Vector<byte>(ba1.AsSpan().Slice(position, vectorSize)),
                    new Vector<byte>(ba2.AsSpan().Slice(position, vectorSize))
                    ))
                    return false;
            }

            // Process any remaining bytes
            for (; position < b1ReadLength; position++)
                if (ba1[position] != ba2[position])
                    return false;

            b1ReadLength = source1.Read(ba1);
        }
        return true;
    }
}