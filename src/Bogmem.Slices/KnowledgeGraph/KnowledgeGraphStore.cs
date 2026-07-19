using Bogmem.Slices.Ids;
using Microsoft.Data.Sqlite;

namespace Bogmem.Slices.KnowledgeGraph;

public sealed record Triple(string Id,string Subject,string Predicate,string Object,DateOnly? ValidFrom,DateOnly? ValidTo,double Confidence=1.0);
public sealed record GraphRow(string Direction,string Subject,string Predicate,string Object,DateOnly? ValidFrom,DateOnly? ValidTo,double Confidence,bool Current);

/// SQLite-backed temporal graph for the four parity operations. A null path is
/// an isolated in-memory database; supplying a path gives the palace durable
/// state and also makes schema/round-trip behavior inspectable with sqlite.
public sealed class KnowledgeGraphStore : IDisposable
{
 private readonly SqliteConnection connection;
 private readonly string recordedAt;
 public KnowledgeGraphStore(string? databasePath=null, DateTime? recordedAt=null)
 {
  this.recordedAt=(recordedAt??DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss",System.Globalization.CultureInfo.InvariantCulture);
  connection=new SqliteConnection(databasePath is null?"Data Source=:memory:":$"Data Source={databasePath}");
  connection.Open();
  using var command=connection.CreateCommand();
  command.CommandText="""
   CREATE TABLE IF NOT EXISTS triples (
    id TEXT PRIMARY KEY, subject TEXT NOT NULL, predicate TEXT NOT NULL,
    object TEXT NOT NULL, valid_from TEXT NULL, valid_to TEXT NULL,
    confidence REAL NOT NULL
   );
   CREATE INDEX IF NOT EXISTS ix_triples_subject ON triples(subject);
   CREATE INDEX IF NOT EXISTS ix_triples_object ON triples(object);
   """;
  command.ExecuteNonQuery();
 }
 public Triple Add(string subject,string predicate,string @object,DateOnly? validFrom=null,double confidence=1)
 {
  if(validFrom is not null&&validFrom.Value<new DateOnly(1900,1,1))throw new ArgumentOutOfRangeException(nameof(validFrom));
  var id=Id(subject,predicate,@object,validFrom,recordedAt);
  using var c=connection.CreateCommand(); c.CommandText="INSERT OR IGNORE INTO triples(id,subject,predicate,object,valid_from,valid_to,confidence) VALUES ($id,$s,$p,$o,$vf,NULL,$confidence)";
  c.Parameters.AddWithValue("$id",id);c.Parameters.AddWithValue("$s",subject);c.Parameters.AddWithValue("$p",predicate);c.Parameters.AddWithValue("$o",@object);c.Parameters.AddWithValue("$vf",(object?)validFrom?.ToString("yyyy-MM-dd")??DBNull.Value);c.Parameters.AddWithValue("$confidence",confidence);c.ExecuteNonQuery();
  return Get(id)!;
 }
 public bool Invalidate(string subject,string predicate,string @object,DateOnly ended)
 {
  var rows=Find(subject,predicate,@object).ToArray(); if(rows.Length==0)return false;
  var t=rows[0];if(t.ValidFrom is not null&&ended<t.ValidFrom)throw new ArgumentException("valid_to precedes valid_from",nameof(ended));
  using var c=connection.CreateCommand();c.CommandText="UPDATE triples SET valid_to=$to WHERE id=$id";c.Parameters.AddWithValue("$to",ended.ToString("yyyy-MM-dd"));c.Parameters.AddWithValue("$id",t.Id);return c.ExecuteNonQuery()==1;
 }
 public IReadOnlyList<Triple> All()=>Read("SELECT id,subject,predicate,object,valid_from,valid_to,confidence FROM triples ORDER BY id");
 public IReadOnlyList<GraphRow> Query(string entity,DateOnly? asOf=null,string direction="outgoing")
 {
  var incoming=string.Equals(direction,"incoming",StringComparison.OrdinalIgnoreCase);
  var rows=Read("SELECT id,subject,predicate,object,valid_from,valid_to,confidence FROM triples WHERE "+(incoming?"lower(object)=lower($entity)":"lower(subject)=lower($entity)")+" ORDER BY valid_from IS NULL, valid_from, predicate",("$entity",entity));
  return rows.Where(t=>Active(t,asOf)).Select(t=>new GraphRow(direction,t.Subject,t.Predicate,t.Object,t.ValidFrom,t.ValidTo,t.Confidence,Current(t,asOf))).ToArray();
 }
 public IReadOnlyList<Triple> Timeline(string entity)=>Read("SELECT id,subject,predicate,object,valid_from,valid_to,confidence FROM triples WHERE lower(subject)=lower($entity) OR lower(object)=lower($entity) ORDER BY valid_from IS NULL, valid_from, id",("$entity",entity));
 private IEnumerable<Triple> Find(string s,string p,string o)=>Read("SELECT id,subject,predicate,object,valid_from,valid_to,confidence FROM triples WHERE subject=$s AND predicate=$p AND object=$o",("$s",s),("$p",p),("$o",o));
 private Triple? Get(string id)=>Read("SELECT id,subject,predicate,object,valid_from,valid_to,confidence FROM triples WHERE id=$id",("$id",id)).FirstOrDefault();
 private List<Triple> Read(string sql,params (string Name,object Value)[] parameters){using var c=connection.CreateCommand();c.CommandText=sql;foreach(var p in parameters)c.Parameters.AddWithValue(p.Name,p.Value);using var r=c.ExecuteReader();var result=new List<Triple>();while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),ParseDate(r.IsDBNull(4)?null:r.GetString(4)),ParseDate(r.IsDBNull(5)?null:r.GetString(5)),r.GetDouble(6)));return result;}
 private static DateOnly? ParseDate(string? value)=>value is null?null:DateOnly.ParseExact(value,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
 private static bool Active(Triple t,DateOnly? d)=>d is null||(t.ValidFrom is null||t.ValidFrom<=d)&&(t.ValidTo is null||d<t.ValidTo);
 private static bool Current(Triple t,DateOnly? d)=>t.ValidTo is null||d is null||d<t.ValidTo;
 public static string Id(string s,string p,string o,DateOnly? validFrom=null,string? recordedAt=null)=>IdRecipes.MakeTripleId(Slug(s),Slug(p),Slug(o),validFrom?.ToString("yyyy-MM-dd"),recordedAt??DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"));
 private static string Slug(string value)=>new(value.ToLowerInvariant().Select(c=>char.IsLetterOrDigit(c)||c=='_'||c=='.'?c:'_').ToArray());
 public void Dispose()=>connection.Dispose();
}
