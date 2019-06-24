﻿using Microsoft.AspNetCore.Builder;
using System;

namespace Microsoft.Extensions.OData.Migration
{
    public static class MigrationExtension
    {
        public static IApplicationBuilder UseMigrationMiddleware(this IApplicationBuilder builder, UriTranslatorOptions options)
        {
            return builder.UseMiddleware<TranslationMiddleware>(options);
        }
    }
}
