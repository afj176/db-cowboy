using System.Security.Cryptography;
using System.Text;

namespace DbCowboy;

public static class ConnectionProfiles
{
    // Session credentials are hashed only in memory; this value is never persisted.
    public static string SessionKey(Profile profile,string password)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ConnectionString(password)+"|readonly="+profile.ReadOnly+"|"+profile.TunnelKey)));
    public static IEnumerable<Profile> Ordered(IEnumerable<Profile> profiles,string search="")=>profiles.Where(p=>string.Join(" ",p.Name,p.Host,p.Database,p.Username,p.Production?"production":"",p.Tag).Contains(search.Trim(),StringComparison.OrdinalIgnoreCase)).OrderByDescending(p=>p.Favorite).ThenByDescending(p=>p.LastConnected).ThenBy(p=>p.Name,StringComparer.OrdinalIgnoreCase);
    public static List<Profile> Upsert(IEnumerable<Profile> profiles,Profile value)=>profiles.Any(p=>p.Id==value.Id)?profiles.Select(p=>p.Id==value.Id?value:p).ToList():profiles.Append(value).ToList();
    public static Profile Duplicate(Profile profile)=>profile with{Id=Guid.NewGuid(),Name=profile.Name+" copy",Favorite=false,LastConnected=null};
    public static Profile WithCredentials(Profile profile,string password,bool? remember)=>profile with{Secret=remember is true?Store.Protect(password):remember is false?"":profile.Secret,AskPassword=remember is true?false:profile.AskPassword};
    /// <summary>The name a connection gets when none is typed.</summary>
    public static string DefaultName(string database,string host)=>(database.Trim().Length>0?database.Trim():"postgres")+" on "+(host.Trim().Length>0?host.Trim():"localhost");
    public static string DefaultTls(string host)=>host.Trim().ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or "[::1]"?"Disable":"VerifyFull";
}

public sealed record PreparedConnection(Db Database,string Version,List<Relation> Relations,List<VectorColumn> Vectors,List<Completion> Completions,List<ForeignKeyInfo> ForeignKeys,Dictionary<VectorColumn,VectorSource> Sources)
{
    public static async Task<PreparedConnection> Open(Profile profile,string password,CancellationToken ct)
    {
        // Prepare a new session completely before the UI replaces the old one.
        var db=await Db.Connect(profile,password,ct);
        try
        {
            var version=await db.Test(ct);var relations=await db.Relations(ct);var vectors=await db.Vectors(ct);var completions=await db.Completions(ct);var edges=await db.ForeignKeys(ct);
            var sources=new Dictionary<VectorColumn,VectorSource>();foreach(var vector in vectors.Take(12))sources[vector]=await db.VectorSource(vector,ct);
            return new(db,version,relations,vectors,completions,edges,sources);
        }
        catch{await db.DisposeAsync();throw;}
    }
}
