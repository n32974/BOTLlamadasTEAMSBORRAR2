using Microsoft.AspNetCore.Authorization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace CallingBotSample.Authentication
{
    public class RoleHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context)
        {
            var pendingRequirements = context.PendingRequirements.ToList();

            foreach (var requirement in pendingRequirements)
            {
                if (requirement is RoleRequirement)
                {
                    if (HasRole(context.User, (RoleRequirement)requirement))
                    {
                        context.Succeed(requirement);
                    }
                }
            }

            //TODO: Use the following if targeting a version of
            //.NET Framework older than 4.6:
            //      return Task.FromResult(0);
            return Task.CompletedTask;
        }

        private bool HasRole(ClaimsPrincipal user, RoleRequirement requirement)
        {
            // Code omitted for brevity
            //if(user.IsInRole)
            if (!user.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == requirement.RoleName))
                return false;
            return true;
        }
    }
}
