namespace pxdSpaceAnalyzer.Core;

/// <summary>A kind of file for the treemap colours and the file-type list.</summary>
public sealed record FileKind(string Name, string Color);

public static class FileTypes
{
    public static readonly FileKind Video = new("Video", "#FF4FA3");
    public static readonly FileKind Audio = new("Music and audio", "#B06BFF");
    public static readonly FileKind Image = new("Pictures", "#FFB347");
    public static readonly FileKind Archive = new("Archives and disk images", "#F2666A");
    public static readonly FileKind Program = new("Programs and libraries", "#35D8FF");
    public static readonly FileKind Document = new("Documents", "#F5E663");
    public static readonly FileKind Code = new("Code and data", "#19F58C");
    public static readonly FileKind Game = new("Game data", "#4D8BFF");
    public static readonly FileKind System = new("System and caches", "#8A92A6");
    public static readonly FileKind Other = new("Other", "#5E6B7E");
    public static readonly FileKind Folder = new("Smaller files", "#3A4255");

    public static readonly FileKind[] All = [Video, Audio, Image, Archive, Program, Document, Code, Game, System, Other];

    private static readonly Dictionary<string, FileKind> Map = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var m = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind k, string list)
        {
            foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) m["." + e] = k;
        }

        Add(Video, "mp4 mkv avi mov wmv m4v webm flv mpg mpeg ts m2ts vob 3gp mts");
        Add(Audio, "mp3 flac wav aac m4a ogg opus wma aiff alac mid midi");
        Add(Image, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng psd svg ico avif");
        Add(Archive, "zip rar 7z tar gz tgz bz2 xz zst cab iso img vhd vhdx vmdk wim esd msu");
        Add(Program, "exe dll sys msi msp ocx drv mui efi appx msix msixbundle appxbundle node so pdb lib");
        Add(Document, "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md epub csv one pst ost eml msg");
        Add(Code, "cs js ts py java c cpp h hpp go rs json xml yaml yml html css sql db sqlite mdb accdb log ps1 bat cmd sh ini cfg toml");
        Add(Game, "pak vpk bsa ba2 upk uasset umap forge rpf bin dat bundle assets resource unity3d wad gcf");
        Add(System, "tmp etl evtx dmp cache lock mum cat manifest blf regtrans-ms edb jrs chk old bak");
        return m;
    }

    public static FileKind Of(string extension) => Map.TryGetValue(extension, out var k) ? k : Other;

    public static FileKind OfName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? Of(name[dot..]) : Other;
    }
}
