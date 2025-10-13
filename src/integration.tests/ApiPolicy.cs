using common;
using CsCheck;
using System.Collections.Immutable;
using System.Linq;

namespace integration.tests;

internal sealed record ApiPolicyModel : IPolicyResourceTestModel<ApiPolicyModel>
{
    public required string Content { get; init; }

    public static IPolicyResource AssociatedResource { get; } = ApiPolicyResource.Instance;

    public static Gen<ModelNodeSet> GenerateNodes(ResourceModels baseline)
    {
        var predecessorsGenOption = from set in baseline.Find(ApiResource.Instance)
                                    let apis = set.Where(node => node.Model is ApiModel apiModel && apiModel.Type is not ApiType.WebSocket)
                                                  .ToImmutableArray()
                                    where apis.Length > 0
                                    select from api in Gen.OneOfConst([.. apis])
                                           select ModelNodeSet.From([api]);

        var option = from predecessorsGen in predecessorsGenOption
                     let newGenerator = from predecessors in predecessorsGen
                                        from model in Generate()
                                        select (model, predecessors)
                     select Generator.GenerateNodes(baseline, GenerateUpdate, newGenerator);

        return option.IfNone(() => Gen.Const(ModelNodeSet.Empty));
    }

    private static Gen<ApiPolicyModel> Generate() =>
        from content in GenerateContent()
        select new ApiPolicyModel
        {
            Content = content
        };

    private static Gen<string> GenerateContent() =>
        from inboundSnippet in Generator.InboundPolicySnippet
        from outboundSnippet in Generator.OutboundPolicySnippet
        select $"""
                <policies>
                    {inboundSnippet}
                    <backend>
                        <forward-request />
                    </backend>
                    {outboundSnippet}
                    <on-error>
                        <base />
                    </on-error>
                </policies>
                """;

    private static Gen<ApiPolicyModel> GenerateUpdate(ApiPolicyModel model) =>
        from content in GenerateContent()
        select model with { Content = content };
}