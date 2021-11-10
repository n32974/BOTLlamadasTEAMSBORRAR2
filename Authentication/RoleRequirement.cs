using Microsoft.AspNetCore.Authorization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CallingBotSample.Authentication
{
    public class RoleRequirement : IAuthorizationRequirement
    {
        public string RoleName { get; }
        public RoleRequirement(string roleName)
        {
            RoleName = roleName;
        }

    }
}
