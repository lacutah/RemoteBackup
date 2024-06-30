using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RemoteSQLBackup;
internal class BackupFileInfo
{
    public string FileName { get; set; } = String.Empty;
    public bool SameAsPrevious { get; set; } = false;
    public bool IsZip { get; init; } = false;
    public DateTime BackupDate { get; init; }
    public bool Keep { get; set; }
}
