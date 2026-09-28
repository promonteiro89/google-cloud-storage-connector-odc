using OutSystems.ExternalLibraries.SDK;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;

[OSStructure(Description = "Google Cloud credentials. Two methods are supported: 'WorkloadIdentityFederation' (recommended, keyless) and 'ServiceAccountKey' (legacy: ClientEmail + PrivateKey). Leave AuthenticationMethod empty to use ServiceAccountKey.")]
public struct Authentication
{
    public Authentication()
    {
        ProjectId = string.Empty;
        AuthenticationMethod = string.Empty;
        ClientEmail = string.Empty;
        PrivateKey = string.Empty;
        WorkloadIdentityProvider = string.Empty;
        ServiceAccountEmail = string.Empty;
        TokenEndpoint = string.Empty;
        ClientId = string.Empty;
        ClientSecret = string.Empty;
        Scope = string.Empty;
        Audience = string.Empty;
        SubjectToken = string.Empty;
    }

    [OSStructureField(Description = "The unique ID of your Google Cloud Project.", IsMandatory = true)]
    public string ProjectId { get; set; }

    [OSStructureField(Description = "'WorkloadIdentityFederation' (recommended, keyless) or 'ServiceAccountKey' (legacy). Empty = ServiceAccountKey.")]
    public string AuthenticationMethod { get; set; }

    // ---- ServiceAccountKey (legacy) ----------------------------------------------------

    [OSStructureField(Description = "ServiceAccountKey only: the 'client_email' from your Service Account JSON key.")]
    public string ClientEmail { get; set; }

    [OSStructureField(Description = "ServiceAccountKey only: the 'private_key' from your Service Account JSON key.")]
    public string PrivateKey { get; set; }

    // ---- WorkloadIdentityFederation (recommended) ---------------------------------------

    [OSStructureField(Description = "WorkloadIdentityFederation: the workload identity provider, e.g. '//iam.googleapis.com/projects/PROJECT_NUMBER/locations/global/workloadIdentityPools/POOL/providers/PROVIDER'.")]
    public string WorkloadIdentityProvider { get; set; }

    [OSStructureField(Description = "WorkloadIdentityFederation: the Google service account the connector acts as, e.g. 'my-sa@my-project.iam.gserviceaccount.com'.")]
    public string ServiceAccountEmail { get; set; }

    [OSStructureField(Description = "WorkloadIdentityFederation: your identity provider's OAuth 2.0 token endpoint (client-credentials grant), e.g. 'https://login.microsoftonline.com/TENANT/oauth2/v2.0/token'. Not needed when SubjectToken is provided.")]
    public string TokenEndpoint { get; set; }

    [OSStructureField(Description = "WorkloadIdentityFederation: the client ID of your app registration at the identity provider.")]
    public string ClientId { get; set; }

    [OSStructureField(Description = "WorkloadIdentityFederation: the client secret of your app registration. Store it as a Secret setting.")]
    public string ClientSecret { get; set; }

    [OSStructureField(Description = "WorkloadIdentityFederation: optional 'scope' for the token request, e.g. 'api://gcs-connector/.default' (Entra ID).")]
    public string Scope { get; set; }

    [OSStructureField(Description = "WorkloadIdentityFederation: optional 'audience' for the token request (required by some providers, e.g. Auth0).")]
    public string Audience { get; set; }

    [OSStructureField(Description = "WorkloadIdentityFederation: optional pre-obtained identity token (JWT). When set, the connector uses it instead of requesting one from TokenEndpoint.")]
    public string SubjectToken { get; set; }
}
