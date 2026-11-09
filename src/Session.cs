using System;
using System.Collections.Generic;

namespace PrinterUtility
{
    /// <summary>
    /// Identity of the current session. With --demo the GUI shows fictitious sample data
    /// (Contoso printers, user, PC and network) – used for documentation screenshots so no real
    /// host names or user names end up in them. Demo mode never changes anything on the PC.
    /// </summary>
    internal static class Session
    {
        public static bool IsDemo { get; private set; }

        /// <summary>null = follow Windows, true = dark, false = light.</summary>
        public static bool? ForceDark { get; private set; }

        public static void EnableDemo(string theme)
        {
            IsDemo = true;
            if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)) ForceDark = false;
            else if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)) ForceDark = true;
        }

        public static string UserName => IsDemo ? "jdoe" : Environment.UserName;
        public static string UserDomain => IsDemo ? "CONTOSO" : Environment.UserDomainName;
        public static string MachineName => IsDemo ? "PC-0427" : Environment.MachineName;
        public static string DomainName => IsDemo ? "contoso.local" : SystemTools.DomainName;

        private const string Server = @"\\printsrv01.contoso.local";

        public static List<PrinterInfo> DemoPrinters()
        {
            const uint local = Native.PRINTER_ATTRIBUTE_LOCAL, shared = Native.PRINTER_ATTRIBUTE_SHARED,
                       network = Native.PRINTER_ATTRIBUTE_NETWORK, published = Native.PRINTER_ATTRIBUTE_PUBLISHED;
            return new List<PrinterInfo>
            {
                new PrinterInfo { Name = "HP LaserJet Pro M404", ShareName = "Accounting-HP", Port = "IP_10.0.20.15", Attributes = local | shared | published,
                                  Driver = "HP LaserJet Pro M404-M405 PCL 6 (V3)", Location = "2nd floor - Accounting" },
                new PrinterInfo { Name = "Brother HL-L2350DW", Port = "USB001", Attributes = local, Driver = "Brother HL-L2350D series" },
                new PrinterInfo { Name = "Microsoft Print to PDF", Port = "PORTPROMPT:", Attributes = local, Driver = "Microsoft Print To PDF" },
                new PrinterInfo { Name = Server + @"\Reception-Color", ServerName = Server, ShareName = "Reception-Color", Attributes = network,
                                  Driver = "Canon iR-ADV C5535 UFR II", Location = "Ground floor - Reception", IsDefault = true },
                new PrinterInfo { Name = Server + @"\HR-Mono", ServerName = Server, ShareName = "HR-Mono", Attributes = network,
                                  Driver = "Kyocera ECOSYS M3655idn KX", Status = 0x400, Jobs = 3 },
                new PrinterInfo { Name = Server + @"\Warehouse-Label", ServerName = Server, ShareName = "Warehouse-Label", Attributes = network,
                                  Driver = "ZDesigner ZT411-203dpi ZPL", Status = 0x80 },
            };
        }

        public static List<PrinterInfo> DemoServerPrinters()
        {
            var list = new List<PrinterInfo>();
            foreach (var (share, driver) in new[]
                     {
                         ("Finance-Color", "HP Color LaserJet MFP M480 PCL-6"), ("HR-Mono", "Kyocera ECOSYS M3655idn KX"),
                         ("IT-Lab", "Brother MFC-L8900CDW series"), ("Reception-Color", "Canon iR-ADV C5535 UFR II"),
                         ("Warehouse-Label", "ZDesigner ZT411-203dpi ZPL"),
                     })
                list.Add(new PrinterInfo { Name = share, ShareName = share, Driver = driver, Attributes = Native.PRINTER_ATTRIBUTE_SHARED });
            return list;
        }

        public static List<SystemTools.NetworkProfile> DemoNetworks() =>
            new List<SystemTools.NetworkProfile> { new SystemTools.NetworkProfile { Name = "contoso.local", Category = 2 } };
    }
}
