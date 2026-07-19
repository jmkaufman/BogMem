namespace Bogmem.Slices.KnowledgeGraph;
public sealed record Triple(string Id,string Subject,string Predicate,string Object,DateOnly? ValidFrom,DateOnly? ValidTo,double Confidence=1.0);
public sealed record GraphRow(string Direction,string Subject,string Predicate,string Object,DateOnly? ValidFrom,DateOnly? ValidTo,double Confidence,bool Current);
public sealed class KnowledgeGraphStore
{
 private readonly Dictionary<string,Triple> triples=new(StringComparer.Ordinal); private readonly HashSet<string> entities=new(StringComparer.OrdinalIgnoreCase);
 public Triple Add(string subject,string predicate,string @object,DateOnly? validFrom=null,double confidence=1){if(validFrom is not null && validFrom.Value<new DateOnly(1900,1,1))throw new ArgumentOutOfRangeException(nameof(validFrom));var id=Id(subject,predicate,@object);if(!triples.ContainsKey(id))triples[id]=new(id,subject,predicate,@object,validFrom,null,confidence);entities.Add(subject);entities.Add(@object);return triples[id];}
 public bool Invalidate(string subject,string predicate,string @object,DateOnly ended){var id=Id(subject,predicate,@object);if(!triples.TryGetValue(id,out var t))return false;if(t.ValidFrom is not null&&ended<t.ValidFrom)throw new ArgumentException("valid_to precedes valid_from",nameof(ended));triples[id]=t with {ValidTo=ended};return true;}
 public IReadOnlyList<Triple> All()=>triples.Values.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray();
 public IReadOnlyList<GraphRow> Query(string entity,DateOnly? asOf=null,string direction="outgoing") {var rows=triples.Values.Where(t=>direction!="incoming"?string.Equals(t.Subject,entity,StringComparison.OrdinalIgnoreCase):string.Equals(t.Object,entity,StringComparison.OrdinalIgnoreCase)).Where(t=>Active(t,asOf)).OrderBy(t=>t.ValidFrom??DateOnly.MinValue).ThenBy(t=>t.Predicate,StringComparer.Ordinal).Select(t=>new GraphRow(direction,t.Subject,t.Predicate,t.Object,t.ValidFrom,t.ValidTo,t.Confidence,Current(t,asOf))).ToArray();return rows;}
 public IReadOnlyList<Triple> Timeline(string entity)=>triples.Values.Where(t=>string.Equals(t.Subject,entity,StringComparison.OrdinalIgnoreCase)||string.Equals(t.Object,entity,StringComparison.OrdinalIgnoreCase)).OrderBy(t=>t.ValidFrom??DateOnly.MinValue).ThenBy(t=>t.Id,StringComparer.Ordinal).ToArray();
 private static bool Active(Triple t,DateOnly? d)=>d is null||(t.ValidFrom is null||t.ValidFrom<=d)&&(t.ValidTo is null||d<t.ValidTo);
 private static bool Current(Triple t,DateOnly? d)=>t.ValidTo is null||d is null||d<t.ValidTo;
 public static string Id(string s,string p,string o)=>$"t_{Slug(s)}_{Slug(p)}_{Slug(o)}_{ShortHash($"{s}\0{p}\0{o}")}";
 private static string Slug(string s)=>new string(s.ToLowerInvariant().Select(c=>char.IsLetterOrDigit(c)?c=='_'?'_':c:'_').ToArray()).Trim('_');
 private static string ShortHash(string s){using var h=System.Security.Cryptography.SHA256.Create();return Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant()[..12];}
}
