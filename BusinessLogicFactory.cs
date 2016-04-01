using Homesite.ECommerce.Context;
using Homesite.ECommerce.ServiceLibrary.Legacy;
using Homesite.IQuote.BusinessLogic;
using Homesite.ECommerce.ServiceLibrary.Cache;
using Homesite.ECommerce.Configuration;

namespace Homesite.ECommerce.ServiceLibrary.BusinessLogic.Factory
{
    [System.Runtime.InteropServices.GuidAttribute("34670558-219F-46A5-A80E-C2F84D0C0DFD")]
    public static class BusinessLogic
    {
        public static Context.IBusinessLogic Get(QuoteHeader qHeader)
        {
            System.Diagnostics.Debug.Assert(qHeader.SessionId > 0, "You need to have a session ID before you get an instance of the business logic.");
            return GetBusinessLogic(qHeader);

        }

        public static Context.IBusinessLogic Get(QuoteHeader qHeader, QuoteData qData)
        {
            return GetBusinessLogic(qHeader, qData);
        }

        public static Context.IBusinessLogic Get(QuoteHeader qHeader, IIQuoteConfiguration config)
        {

            System.Diagnostics.Debug.Assert(qHeader.SessionId > 0, "You need to have a session ID before you get an instance of the business logic because of the cache.");

            if (qHeader.FormCode == 3 || qHeader.FormCode == 2)
            {
                return GetBusinessLogic(qHeader);
            }
            return GetLegacyBusinessLogic(qHeader);
        }


        public static void UpdateSessionCache(int sessionId, Context.IBusinessLogic bl)
        {

            if(bl.QHeader.PartnerId.ToString() == "1032")
            {
                if(MPQBusinessLogicCache.StaticCollection.ContainsSessionBusinessLogic(MPQBusinessLogicCache.StaticCollection.GetCacheKey(sessionId.ToString())))
                {
                    MPQBusinessLogicCache.StaticCollection.UpdateSessionBusinessLogic(bl);
                }
            }

            else if (BusinessLogicCache.StaticCollection.ContainsSessionBusinessLogic(BusinessLogicCache.StaticCollection.GetCacheKey(sessionId.ToString())))
            {
                BusinessLogicCache.StaticCollection.UpdateSessionBusinessLogic(bl);
            }
        }
        
/*
        private static Context.IBusinessLogic GetLegacyBusinessLogic(QuoteHeader qHeader, QuoteData qData)
        {
            return new LegacyConsumerBusinessLogic<ProgressiveBusinessLogic>(qData, qHeader);
        }
*/

        private static Context.IBusinessLogic GetBusinessLogic(QuoteHeader qHeader, QuoteData qData)
        {
            TimerCache.StaticCollection.SaveSessionDateTime(qHeader.SessionId);
            return BusinessLogicCache.StaticCollection.GetSessionBusinessLogic(qHeader, qData);
        }

        private static Context.IBusinessLogic GetLegacyBusinessLogic(QuoteHeader qHeader)
        {
            return new LegacyConsumerBusinessLogic<ProgressiveBusinessLogic>(qHeader);
        }

        private static Context.IBusinessLogic GetBusinessLogic(QuoteHeader qHeader)
        {
            TimerCache.StaticCollection.SaveSessionDateTime(qHeader.SessionId);
            return BusinessLogicCache.StaticCollection.GetSessionBusinessLogic(qHeader);
        }

/*
        private static Context.IBusinessLogic GetBusinessLogic(int sessionId)
        {
            return BusinessLogicCache.StaticCollection.GetSessionBusinessLogic(sessionId);
        }
*/

    }
}
