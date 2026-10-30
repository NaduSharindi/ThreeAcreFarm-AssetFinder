using System.ServiceModel;
using PrimaFarm.AssetFinder.Web.Models;
using PrimaFarm.AssetFinder.Web.Services.Assets;

namespace PrimaFarm.AssetFinder.Web.Services.Soap;

/// <summary>
/// SOAP 1.1 endpoint for legacy consumers (e.g. the ERP / VB6 front ends that cannot call REST).
/// WSDL: /soap/AssetLookup.asmx?wsdl. Authenticated with the X-Api-Key header, like the REST API.
/// </summary>
[ServiceContract(Namespace = ApiNamespaces.Assets)]
public interface IAssetLookupSoapService
{
    [OperationContract]
    Task<AssetDto?> GetAsset(long assetCode);

    [OperationContract]
    Task<FarmSummary[]> GetFarmSummary();
}

public class AssetLookupSoapService(AssetService assets) : IAssetLookupSoapService
{
    public Task<AssetDto?> GetAsset(long assetCode) => assets.GetAsync(assetCode, CancellationToken.None);

    public async Task<FarmSummary[]> GetFarmSummary() => [.. await assets.GetFarmSummaryAsync(CancellationToken.None)];
}
