using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using RestSharp.Serializers.NewtonsoftJson;
using EstrellasDeEsperanza.AsyncLock;

namespace HostPanelPro.Providers.DNS.Technitium;


/// <summary>
/// Thin typed client for the Technitium DNS Server HTTP API
/// (see APIDocumentation.md in this project). Handles session login/token caching;
/// callers work with plain .NET types and don't deal with the JSON envelope directly.
/// </summary>
public class Api
{
    readonly string baseUrl;
    readonly string user;
    readonly string password;
    readonly AsyncLock authLock = new AsyncLock();

    RestClient client;
    string token;

    public Api(string serverUrl, string adminUser, string adminPassword)
    {
        if (string.IsNullOrEmpty(serverUrl))
            throw new ArgumentException("Technitium DNS server URL is not configured.", nameof(serverUrl));
        if (string.IsNullOrEmpty(adminPassword))
            throw new ArgumentException("Technitium DNS admin password is not configured.", nameof(adminPassword));
        if (string.IsNullOrEmpty(adminUser))
            throw new ArgumentException("Technitium DNS admin user is not configured.", nameof(adminUser));

        baseUrl = serverUrl.TrimEnd('/');
        user = adminUser;
        password = adminPassword;
    }

    RestClient Client => client ??= new RestClient(
        new RestClientOptions(baseUrl),
        configureSerialization: s => s.UseNewtonsoftJson());

    public static void AddParams(RestRequest request, IDictionary<string, string> parameters)
    {
        if (parameters == null)
            return;

        foreach (KeyValuePair<string, string> param in parameters)
        {
            if (param.Value != null)
                request.AddQueryParameter(param.Key, param.Value);
        }
    }
    public static void AddParams(RestRequest request, object parameters)
    {
        if (parameters == null)
            return;
        foreach (var prop in parameters.GetType().GetProperties())
        {
            var value = prop.GetValue(parameters);
            if (value != null)
            {
                if (value is bool) request.AddQueryParameter(prop.Name, value.ToString().ToLowerInvariant());
                else request.AddQueryParameter(prop.Name, value.ToString());

            }
        }
    }

    #region Low level request execution

    public async Task EnsureLoggedInAsync()
    {
        if (token != null)
            return;

        using (await authLock.LockAsync())
        {
            if (token != null)
                return;

            RestRequest request = new RestRequest("/api/user/login", Method.Get);
            request.AddQueryParameter("user", user);
            request.AddQueryParameter("pass", password);

            LoginResult result = await ExecuteFlatAsync<LoginResult>(request, requireAuth: false);
            token = result.token;
        }
    }

    public async Task<RestResponse> ExecuteRawAsync(RestRequest request, bool requireAuth = true)
    {
        if (requireAuth)
        {
            await EnsureLoggedInAsync();
            request.AddOrUpdateParameter("token", token);
        }

        RestResponse response = request.Method == Method.Post
            ? await Client.ExecutePostAsync(request)
            : await Client.ExecuteGetAsync(request);

        if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
        {
            throw new TechnitiumApiException(
                "error",
                string.Format("Technitium DNS server request to '{0}' failed: {1} {2}", request.Resource, response.StatusCode, response.ErrorMessage),
                response.ErrorException?.ToString(),
                null);
        }

        return response;
    }

    /// <summary>
    /// Executes a call whose payload is nested under a "response" property and returns
    /// that payload. Retries once, transparently re-authenticating, on "invalid-token".
    /// </summary>
    public async Task<T> ExecuteAsync<T>(RestRequest request, bool requireAuth = true, bool allowRetry = true)
    {
        RestResponse response = await ExecuteRawAsync(request, requireAuth);

        ApiResult<T> result = Deserialize<ApiResult<T>>(response.Content);

        if (result.status == "invalid-token" && requireAuth && allowRetry)
        {
            token = null;
            return await ExecuteAsync<T>(request, requireAuth, allowRetry: false);
        }

        if (result.status != "ok")
            throw new TechnitiumApiException(result.status, result.errorMessage, result.stackTrace, result.innerErrorMessage);

        return result.response;
    }

    /// <summary>
    /// Executes a call whose payload sits at the top level alongside "status" (login,
    /// createToken, the status-only responses to delete/set calls, etc).
    /// </summary>
    public async Task<T> ExecuteFlatAsync<T>(RestRequest request, bool requireAuth = true, bool allowRetry = true)
        where T : ApiResult
    {
        RestResponse response = await ExecuteRawAsync(request, requireAuth);

        T result = Deserialize<T>(response.Content);

        if (result.status == "invalid-token" && requireAuth && allowRetry)
        {
            token = null;
            return await ExecuteFlatAsync<T>(request, requireAuth, allowRetry: false);
        }

        if (result.status != "ok")
            throw new TechnitiumApiException(result.status, result.errorMessage, result.stackTrace, result.innerErrorMessage);

        return result;
    }

    public static T Deserialize<T>(string content)
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(content);
        }
        catch (Exception ex)
        {
            throw new TechnitiumApiException("error", "Unable to parse Technitium DNS server response.", ex.ToString(), null);
        }
    }

    #endregion

    public Task LoginAsync()
    {
        token = null;
        return EnsureLoggedInAsync();
    }

    public async Task LogoutAsync()
    {
        if (token == null)
            return;

        RestRequest request = new RestRequest("/api/user/logout", Method.Get);
        await ExecuteFlatAsync<ApiResult>(request);
        token = null;
    }

}
