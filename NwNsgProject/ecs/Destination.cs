using System;
public class Destination
{
    public string address { get; set; }
    public string ip { get; set; }
    public long port { get; set; }
    public long packets { get; set; }
    public long bytes { get; set; }
    
    public Destination(DenormalizedRecord denormalizedRecord)
    {
        this.address = denormalizedRecord.destinationAddress;
        this.ip = denormalizedRecord.destinationAddress;
        this.port = Convert.ToInt64(denormalizedRecord.destinationPort);
        this.packets = Convert.ToInt64(denormalizedRecord.packetsDtoS);
        this.bytes = Convert.ToInt64(denormalizedRecord.bytesDtoS);
    }
}
