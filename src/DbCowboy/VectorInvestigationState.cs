using System.Text.Json;

namespace DbCowboy;

public static class VectorInvestigationState
{
    public static bool Compatible(VectorSource source,VectorBookmark? bookmark)
    {
        if(source.Keys is not {Length:>0}||bookmark?.Keys is not {} keys||!source.Keys.Order().SequenceEqual(keys.Keys.Order()))return false;
        foreach(string value in keys.Values){if(string.IsNullOrEmpty(value)||value.Length>16384)return false;try{using var doc=JsonDocument.Parse(value);}catch(JsonException){return false;}}
        return true;
    }
    public static VectorBookmark? Bookmark(VectorSource? source,VectorItem? item)
    {
        if(source?.Keys is not {Length:>0} || item==null)return null;
        using var doc=JsonDocument.Parse(item.Metadata);var keys=new Dictionary<string,string>();
        foreach(string key in source.Keys){if(!doc.RootElement.TryGetProperty(key,out var value))return null;keys[key]=value.GetRawText();}
        return new(keys);
    }
    public static bool Matches(VectorSource source,VectorBookmark? bookmark,VectorItem item)
    {
        if(bookmark==null||!Compatible(source,bookmark))return false;var candidate=Bookmark(source,item);
        return candidate!=null&&candidate.Keys.Count==bookmark.Keys.Count&&candidate.Keys.All(k=>bookmark.Keys.GetValueOrDefault(k.Key)==k.Value);
    }
}
