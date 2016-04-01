using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Homesite.ECommerce.Context;
using Homesite.Diagnostics.ECommerce;
using Homesite.ECommerce.WebServices.QuoteServicesRef;

namespace Homesite.ECommerce.WebServices
{
    public class QuoteService : WebServicesClientBase<QuoteServicesClient, QuoteServices>
    {
        public QuoteService()
        {
        }

        public void ProcessPartialMatchModal(Dictionary<string, string> qHeader)
        {
            try
            {
                this.GetQuoteHeaderServiceClient(qHeader).ProcessPartialMatchModal(qHeader);
            }
            finally
            {
                this.CloseServiceClient();
            }
        }

        public void ProcessNoMatchModal(Dictionary<string, string> qHeader)
        {
            try
            {
                this.GetQuoteHeaderServiceClient(qHeader).ProcessNoMatchModal(qHeader);
            }
            finally
            {
                this.CloseServiceClient();
            }
        }


    }
}
