namespace Netimobiledevice.Backup
{
    /// <summary>
    /// EventArgs for File Transfer Error events.
    /// </summary>
    public class BackupFileErrorEventArgs : BackupFileEventArgs
    {
        /// <summary>
        /// Indicates whether the backup should be cancelled.
        /// </summary>
        public bool Cancel { get; set; }

#region TEC
        public BackupFileErrorEventArgs(BackupFile file, string details) : base(file) // MEF: Added details
        {
            Details = details;
        }
#endregion

        public string Details { get; set; }
    }
}
