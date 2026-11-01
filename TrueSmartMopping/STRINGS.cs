namespace TrueSmartMopping;

public static class STRINGS
{
    public static class TOOLS
    {
        public static class SMARTMOP
        {
            public static LocString NAME = "Smart Mopping";
            public static LocString TOOLTIP = "Direct duplicants to efficiently mop up liquid from the edges.";
        }
    }

    public static class OVERLAY
    {
        public static LocString NAME = "Auto Mopping";

        public static LocString TOOLTIP =
            "Duplicants automatically sweep up liquid when they touch it. You can configure blocked areas and priority settings.";

        public static class ENABLED
        {
            public static LocString NAME = "Enable auto-mopping";
            public static LocString TOOLTIP = "Duplicants automatically sweep up liquid when they touch it.";
        }

        public static class LIQUID
        {
            public static LocString TOOLTIP = "Include this liquid in auto-mopping.";
        }

        public static class DESC
        {
            public static LocString LINE_0 = "Duplicants automatically sweep up liquid when they touch it.";
            public static LocString LINE_1 = "Drag to define a region and create a no-mop zone for that area.";
            public static LocString LINE_2 = "Drag again inside the restricted area to remove it.";
            public static LocString LINE_3 = "This area will be excluded from auto-mopping.";
        }

        public static class SECTION
        {
            public static LocString BASIC = "[Basic Settings]";
            public static LocString PRIORITY = "[Priority]";
            public static LocString BLOCKED = "[Blocked Areas]";
            public static LocString TARGET = "[Target Liquids]";
        }

        public static class CLEAR
        {
            public static LocString FORMAT = "Clear all blocked areas ({0} cells)";
            public static LocString TOOLTIP = "Remove every auto-mop restricted area defined by dragging.";
        }
    }
}
