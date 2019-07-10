using Microsoft.AspNet.OData.Formatter;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.OData;
using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Extensions.OData.Migration
{
    public class ODataMigrationOutputFormatter : ODataOutputFormatter
    {
        public ODataMigrationOutputFormatter(IEnumerable<ODataPayloadKind> payloadKinds)
            : base(payloadKinds)
        {
        }

        public override bool CanWriteResult(OutputFormatterCanWriteContext context)
        {
            bool isODataV3 = context.HttpContext.Request.Headers["odata-service-VERSION-TODO"] == "3.0";
            return isODataV3 && base.CanWriteResult(context);
        }
    }
}
