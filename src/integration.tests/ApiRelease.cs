using common;
using CsCheck;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace integration.tests;

internal sealed record ApiReleaseModel : IResourceWithReferenceTestModel<ApiReleaseModel>
{
    public required ResourceName Name { get; init; }
    public required string Notes { get; init; }

    public static IResourceWithReference AssociatedResource { get; } = ApiReleaseResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var predecessorsGenOption = from set in baseline.Find(ApiResource.Instance)
                                    let currentApis = set.Where(node => ApiRevisionModule.IsRootName(node.Model.Name))
                                                         .ToImmutableArray()
                                    where currentApis.Length > 0
                                    select from api in Gen.OneOfConst([.. currentApis])
                                           select ModelNodeSet.From([api]);

        var option = from predecessorsGen in predecessorsGenOption
                     let newGenerator = from predecessors in predecessorsGen
                                        from model in Generate()
                                        select (model, predecessors)
                     select Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);

        return option.IfNone(() => Gen.Const(ModelNodeSet.Empty));
    }

    private static Gen<ApiReleaseModel> GenerateUpdate(ApiReleaseModel model) =>
        from notes in GenerateNotes().OrConst(model.Notes)
        select model with
        {
            Notes = notes
        };

    private static Gen<string> GenerateNotes() =>
        from lorem in Generator.Lorem
        select lorem.Sentence();

    private static Gen<ApiReleaseModel> Generate() =>
        from name in Generator.ResourceName
        from notes in GenerateNotes()
        select new ApiReleaseModel
        {
            Name = name,
            Notes = notes
        };

    public JsonObject SerializeDto(ModelNodeSet predecessors) =>
        JsonObjectModule.From(new ApiReleaseDto()
        {
            Properties = new ApiReleaseDto.ApiReleaseContract
            {
                ApiId = predecessors.Single(node => node.Model.AssociatedResource is ApiResource)
                                    .ToResourceId(),
                Notes = Notes
            }
        }, AssociatedResource.SerializerOptions).IfErrorThrow();

    public bool MatchesDto(JsonObject json, Option<JsonObject> overrideJson)
    {
        var jsonDto = JsonNodeModule.To<ApiReleaseDto>(json, AssociatedResource.SerializerOptions)
                                    .IfErrorNull();

        var overrideDto = overrideJson.Bind(json => JsonNodeModule.To<ApiReleaseDto>(json, AssociatedResource.SerializerOptions)
                                                                  .ToOption())
                                      .IfNoneNull();

        var left = new
        {
            Notes = overrideDto?.Properties?.Notes ?? Notes
        };

        var right = new
        {
            Notes = jsonDto?.Properties?.Notes
        };

        return left.Notes.FuzzyEquals(right.Notes);
    }
}
