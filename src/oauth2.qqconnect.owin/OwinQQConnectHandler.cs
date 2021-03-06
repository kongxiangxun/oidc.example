using System;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Infrastructure;
using OAuth2.QQConnect.Basic;

namespace OAuth2.QQConnect.Owin
{
    public class OwinQQConnectHandler : AuthenticationHandler<OwinQQConnectOptions>
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;

        private QQConnectHandler _innerHandler;

        private QQConnectHandler InnerHandler
        {
            get
            {
                if (_innerHandler == null)
                {
                    var qqConnectOptions = Options.BuildQQConnectOptions(GetRedirectUrl);
                    _innerHandler = new QQConnectHandler(_httpClient, qqConnectOptions);
                }
                return _innerHandler;
            }
        }

        public OwinQQConnectHandler(ILogger logger, HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        public override async Task<bool> InvokeAsync()
        {
            if (Options.CallbackPath != Request.Path.Value)
            {
                return false;
            }
            var ticket = await AuthenticateAsync();
            if (ticket?.Identity != null)
            {
                var identity = ticket.Identity;
                if (identity.AuthenticationType != Options.SignInAsAuthenticationType)
                {
                    identity = new ClaimsIdentity(
                        ticket.Identity.Claims,
                        Options.SignInAsAuthenticationType,
                        ticket.Identity.NameClaimType,
                        ticket.Identity.RoleClaimType);
                }

                Context.Authentication.SignIn(ticket.Properties, identity);

                Context.Response.Redirect(ticket.Properties.RedirectUri);
            }
            else
            {
                _logger.WriteError("Invalid return state, unable to redirect.");
                Response.StatusCode = 500;
            }

            return true;
        }

        protected override async Task<AuthenticationTicket> AuthenticateCoreAsync()
        {
            AuthenticationProperties properties = null;

            try
            {
                var code = Request.Query.Get("code");
                var state = Request.Query.Get("state");

                properties = Options.StateDataFormat.Unprotect(state);
                if (properties == null)
                {
                    return null;
                }

                if (!ValidateCorrelationId(properties, _logger))
                {
                    return new AuthenticationTicket(null, properties);
                }

                if (code == null)
                {
                    return new AuthenticationTicket(null, properties);
                }

                var token = await InnerHandler.GetTokenAsync(
                    code,
                    Request.CallCancelled);

                if (string.IsNullOrWhiteSpace(token.AccessToken))
                {
                    _logger.WriteError("access_token was not found");
                    return new AuthenticationTicket(null, properties);
                }


                var openId = await InnerHandler.GetOpenIdAsync(
                    token.AccessToken,
                    Request.CallCancelled);

                if (string.IsNullOrWhiteSpace(openId.OpenId))
                {
                    _logger.WriteError("openid was not found");
                    return new AuthenticationTicket(null, properties);
                }


                var user = await InnerHandler.GetUserAsync(
                    token.AccessToken,
                    openId.OpenId,
                    Request.CallCancelled);

                var identity = QQConnectProfile.BuildClaimsIdentity(Options.AuthenticationType, token, openId, user);

                return new AuthenticationTicket(identity, properties);
            }
            catch (Exception ex)
            {
                _logger.WriteError("Authentication failed", ex);
                return new AuthenticationTicket(null, properties);
            }
        }

        protected override Task ApplyResponseChallengeAsync()
        {
            if (Response.StatusCode != 401)
            {
                return Task.FromResult<object>(null);
            }

            var authenticationChallenge = Helper.LookupChallenge(Options.AuthenticationType, Options.AuthenticationMode);

            if (authenticationChallenge != null)
            {
                var authenticationProperties = authenticationChallenge.Properties;
                if (string.IsNullOrWhiteSpace(authenticationProperties.RedirectUri))
                {
                    authenticationProperties.RedirectUri = Request.Uri.ToString();
                }

                GenerateCorrelationId(authenticationProperties);

                var authorizationUrl = BuildAuthorizationUrl(authenticationProperties);

                Context.Response.Redirect(authorizationUrl);
            }

            return Task.FromResult<object>(null);
        }

        private string BuildAuthorizationUrl(AuthenticationProperties authenticationProperties)
        {
            var qqConnectProperties = authenticationProperties.Dictionary.GetQQConnectProperties();
            authenticationProperties.Dictionary.RemoveQQConnectProperties();

            var state = Options.StateDataFormat.Protect(authenticationProperties);

            return InnerHandler.BuildAuthorizationUrl(qqConnectProperties, state);
        }

        private string GetRedirectUrl()
        {
            return Request.Scheme +
                   Uri.SchemeDelimiter +
                   Request.Host +
                   Request.PathBase +
                   Options.CallbackPath;
        }
    }
}