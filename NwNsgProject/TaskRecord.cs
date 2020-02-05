using System;
public class TaskRecord
{
    public int bytesSent { get; set; }
    public int eventCount { get; set; }
    public TaskRecord(int bytesSent, int eventCount)
    {
       this.bytesSent = bytesSent;
       this.eventCount = eventCount;
    }

    public void AddTaskRecord(TaskRecord taskRecord)
    {
        if (taskRecord != null)
        {
            bytesSent += taskRecord.bytesSent;
            eventCount += taskRecord.eventCount;
        }        
    }
}
