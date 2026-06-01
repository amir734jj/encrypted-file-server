using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace Api.Interfaces;

public interface IEntity
{
    [Key]
    public Guid Id { get; set; }

    private static readonly JsonSerializerSettings JsonSerializerSettings = new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        Formatting = Formatting.Indented
    };

    public string ToJsonString()
    {
        return JsonConvert.SerializeObject(this, JsonSerializerSettings);
    }
}
