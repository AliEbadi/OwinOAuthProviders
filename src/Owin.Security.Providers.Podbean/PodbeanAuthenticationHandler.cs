﻿#region

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Owin;
using System.Net.Http.Headers;

#endregion

namespace Owin.Security.Providers.Podbean
{
	public class PodbeanAuthenticationHandler : AuthenticationHandler<PodbeanAuthenticationOptions>
	{
		private const string XmlSchemaString = "http://www.w3.org/2001/XMLSchema#string";
		private const string TokenEndpoint = "https://api.podbean.com/v1/oauth/token";
		private const string PodcastIdEndpoint = "https://api.podbean.com/v1/oauth/debugToken";
		private const string PodcastEndpoint = "https://api.podbean.com/v1/podcast";
		private readonly HttpClient _httpClient;

		private readonly ILogger _logger;

		public PodbeanAuthenticationHandler(HttpClient httpClient, ILogger logger)
		{
			_httpClient = httpClient;
			_logger = logger;
		}

		protected override async Task<AuthenticationTicket> AuthenticateCoreAsync()
		{
			AuthenticationProperties properties = null;

			try
			{
				string code = null;
				string state = null;

				var query = Request.Query;
				var values = query.GetValues("code");
				if (values != null && values.Count == 1)
					code = values[0];
				values = query.GetValues("state");
				if (values != null && values.Count == 1)
					state = values[0];

				properties = Options.StateDataFormat.Unprotect(state);
				if (properties == null)
					return null;

				// OAuth2 10.12 CSRF
				if (!ValidateCorrelationId(properties, _logger))
					return new AuthenticationTicket(null, properties);

				var requestPrefix = GetBaseUri(Request);
				var redirectUri = requestPrefix + Request.PathBase + Options.CallbackPath;

				// Build up the body for the token request
				var body = new List<KeyValuePair<string, string>>
				{
					new KeyValuePair<string, string>("grant_type", "authorization_code"),
					new KeyValuePair<string, string>("code", code),
					new KeyValuePair<string, string>("redirect_uri", redirectUri)
				};

				// Request the token
				var tokenRequest = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
				tokenRequest.Headers.Authorization = 
					new AuthenticationHeaderValue("Basic", Base64Encode($"{Options.AppId}:{Options.AppSecret}"));
				tokenRequest.Content = new FormUrlEncodedContent(body);

				var tokenResponse = await _httpClient.SendAsync(tokenRequest, Request.CallCancelled);
				tokenResponse.EnsureSuccessStatusCode();
				var text = await tokenResponse.Content.ReadAsStringAsync();

				// Deserializes the token response
				dynamic token = JsonConvert.DeserializeObject<dynamic>(text);
				var accessToken = (string)token.access_token;
				var refreshToken = (string)token.refresh_token;
				var expires = (string)token.expires_in;
				
				// Get the Podbean podcast
				var podcastResponse = await _httpClient.GetAsync(
					$"{PodcastEndpoint}?access_token={Uri.EscapeDataString(accessToken)}", Request.CallCancelled);
				podcastResponse.EnsureSuccessStatusCode();
				text = await podcastResponse.Content.ReadAsStringAsync();
				var podcast = JObject.Parse(text)["podcast"].ToObject<Podcast>();

				// Get the Podbean podcast id
				var podcastIdRequest = new HttpRequestMessage(HttpMethod.Get, $"{PodcastIdEndpoint}?access_token={Uri.EscapeDataString(accessToken)}");
				podcastIdRequest.Headers.Authorization =
					new AuthenticationHeaderValue("Basic", Base64Encode($"{Options.AppId}:{Options.AppSecret}"));
				var podcastIdResponse = await _httpClient.SendAsync(podcastIdRequest, Request.CallCancelled);
				podcastIdResponse.EnsureSuccessStatusCode();
				text = await podcastIdResponse.Content.ReadAsStringAsync();
				var podcastId = JsonConvert.DeserializeObject<dynamic>(text);
				podcast.Id = (string)podcastId.podcast_id;

				var context = new PodbeanAuthenticatedContext(Context, podcast, accessToken, refreshToken, expires)
				{
					Identity = new ClaimsIdentity(
						Options.AuthenticationType,
						ClaimsIdentity.DefaultNameClaimType,
						ClaimsIdentity.DefaultRoleClaimType)
				};
				if (!string.IsNullOrEmpty(context.Id))
					context.Identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, context.Id, XmlSchemaString,
						Options.AuthenticationType));
				if (!string.IsNullOrEmpty(context.Name))
					context.Identity.AddClaim(new Claim(ClaimsIdentity.DefaultNameClaimType, context.Name,
						XmlSchemaString, Options.AuthenticationType));
				context.Properties = properties;

				await Options.Provider.Authenticated(context);

				return new AuthenticationTicket(context.Identity, context.Properties);
			}
			catch (Exception ex)
			{
				_logger.WriteError(ex.Message);
			}
			return new AuthenticationTicket(null, properties);
		}

		protected override Task ApplyResponseChallengeAsync()
		{
			if (Response.StatusCode != 401)
				return Task.FromResult<object>(null);

			var challenge = Helper.LookupChallenge(Options.AuthenticationType, Options.AuthenticationMode);

			if (challenge == null) return Task.FromResult<object>(null);
			var baseUri = GetBaseUri(Request);

			var currentUri =
				baseUri +
				Request.Path +
				Request.QueryString;

			var redirectUri =
				baseUri +
				Options.CallbackPath;

			var properties = challenge.Properties;
			if (string.IsNullOrEmpty(properties.RedirectUri))
				properties.RedirectUri = currentUri;

			// OAuth2 10.12 CSRF
			GenerateCorrelationId(properties);

			var state = Options.StateDataFormat.Protect(properties);

			var scope = string.Join(" ", Options.Scope);

			var authorizationEndpoint =
				"https://api.podbean.com/v1/dialog/oauth" +
				"?response_type=code" +
				"&client_id=" + Uri.EscapeDataString(Options.AppId) +
				"&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
				"&scope=" + Uri.EscapeDataString(scope) +
				"&state=" + Uri.EscapeDataString(state);

			Response.Redirect(authorizationEndpoint);

			return Task.FromResult<object>(null);
		}

		public override async Task<bool> InvokeAsync()
		{
			return await InvokeReplyPathAsync();
		}

		private async Task<bool> InvokeReplyPathAsync()
		{
			if (!Options.CallbackPath.HasValue || Options.CallbackPath != Request.Path) return false;
			// TODO: error responses

			var ticket = await AuthenticateAsync();
			if (ticket == null)
			{
				_logger.WriteWarning("Invalid return state, unable to redirect.");
				Response.StatusCode = 500;
				return true;
			}

			var context = new PodbeanReturnEndpointContext(Context, ticket)
			{
				SignInAsAuthenticationType = Options.SignInAsAuthenticationType,
				RedirectUri = ticket.Properties.RedirectUri
			};

			await Options.Provider.ReturnEndpoint(context);

			if (context.SignInAsAuthenticationType != null &&
				context.Identity != null)
			{
				var grantIdentity = context.Identity;
				if (!string.Equals(grantIdentity.AuthenticationType, context.SignInAsAuthenticationType,
					StringComparison.Ordinal))
					grantIdentity = new ClaimsIdentity(grantIdentity.Claims, context.SignInAsAuthenticationType,
						grantIdentity.NameClaimType, grantIdentity.RoleClaimType);
				Context.Authentication.SignIn(context.Properties, grantIdentity);
			}

			if (context.IsRequestCompleted || context.RedirectUri == null) return context.IsRequestCompleted;
			var redirectUri = context.RedirectUri;
			if (context.Identity == null)
				redirectUri = WebUtilities.AddQueryString(redirectUri, "error", "access_denied");
			Response.Redirect(redirectUri);
			context.RequestCompleted();

			return context.IsRequestCompleted;
		}

		private static string Base64Encode(string plainText)
		{
			var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
			return System.Convert.ToBase64String(plainTextBytes);
		}

		private string GetBaseUri(IOwinRequest request)
		{
			if (Options.DebugUsingRequestHeadersToBuildBaseUri &&
				request.Headers["X-Original-Host"] != null &&
				request.Headers["X-Forwarded-Proto"] != null)
			{
				return request.Headers["X-Forwarded-Proto"] + Uri.SchemeDelimiter + request.Headers["X-Original-Host"];
			}

			var baseUri =
				request.Scheme +
				Uri.SchemeDelimiter +
				request.Host +
				request.PathBase;

			return baseUri;
		}
	}
}