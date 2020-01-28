using System;
public class Source
{
    public string address { get; set; }
    public string ip { get; set; }
    public long port { get; set; }
    public long packets { get; set; }
    public long bytes { get; set; }
    
    public Source(DenormalizedRecord denormalizedRecord)
    {
        this.address = denormalizedRecord.sourceAddress;
        this.ip = denormalizedRecord.sourceAddress;
        this.port = Convert.ToInt64(denormalizedRecord.sourcePort);
        this.packets = Convert.ToInt64(denormalizedRecord.packetsStoD);
        this.bytes = Convert.ToInt64(denormalizedRecord.bytesStoD);
    }
}
