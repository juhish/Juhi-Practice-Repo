using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.ServiceModel;
using System.ServiceModel.Activation;
using System.Threading;
using System.Web;
using System.Xml.Linq;
using Homesite.Diagnostics.Context;
using Homesite.Diagnostics.ECommerce;
using Homesite.Diagnostics.ECommerce.Cache;
using Homesite.ECommerce.Configuration;
using Homesite.ECommerce.Context;
using Homesite.ECommerce.DAC;
using Homesite.ECommerce.IQuote;
using Homesite.ECommerce.ServiceLibrary.BusinessLogic;
using Homesite.ECommerce.ServiceLibrary.Cache;
using Homesite.ECommerce.ServiceLibrary.Exceptions;
using Homesite.ECommerce.ServiceLibrary.ServiceContracts;
using Homesite.IQuote.ISOBroker;
using Homesite.IQuote.LookupBroker;
using ServiceHost = Homesite.ECommerce.IQuote.ServiceHost;
using System.Text;

namespace Homesite.ECommerce.ServiceLibrary
{
    [AspNetCompatibilityRequirements(RequirementsMode = AspNetCompatibilityRequirementsMode.Allowed)]
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.PerCall)]
    public class IntegrationServices : ServiceBase<IIntegrationServices>, IIntegrationServices
    {
        private MPQBusinessLogic _businessLogic;
        private readonly int _mpqPid;

        const int FlashExperience = 1;
        const int FQExperience = 2;
        const int DAQExperience = 3;

        public IntegrationServices()
        {
            CacheSet<ConfigurationCache, string, StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Initialize();
            _mpqPid = CacheSet<ConfigurationCache, string, StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Get().PartnerId;
        }

        #region unit testing / debug

#if DEBUG
        /// <summary>
        ///   FOR UNIT TESTING ONLY!
        /// </summary>
        public MPQBusinessLogic GetBusinessLogic(int sessionId)
        {
            var bl = (MPQBusinessLogic) BusinessLogicCache.StaticCollection.GetSessionBusinessLogic(sessionId);
            return bl;
        }
#endif

#if DEBUG
        /// <summary>
        ///   FOR UNIT TESTING ONLY!
        /// </summary>
        /// <param name = "sessionId"></param>
        public void DisableGaming(int sessionId)
        {
            var bl = (MPQBusinessLogic) BusinessLogicCache.StaticCollection.GetSessionBusinessLogic(sessionId);
            bl.DisableGaming();
        }
#endif

        #endregion

        public string GetHost(Guid key)
        {
            var mpqid = DirectWebDAC.GetMpqIdFromEntranceKey(key);
            var mpqSession = MPQSession.GetMPQSession(mpqid);
            return mpqSession.ServiceHost;
        }


        public string GetHostForMpqNumber(int mpqNumber)
        {
            var mpqId = DirectWebDAC.GetMpqIdFromMpqNumber(mpqNumber);
            var mpqSession = MPQSession.GetMPQSession(mpqId);
            return mpqSession.ServiceHost;
        }

        public MPQQuoteData GetQuoteForEntranceKey(Guid key)
        {
            var retv = new MPQQuoteData();

            var mpqid = DirectWebDAC.GetMpqIdFromEntranceKey(key);

            var qHdr = new QuoteHeader();
            var qData = new QuoteData();

            var mpqSession = MPQSession.GetMPQSession(mpqid);
            
                qHdr.SessionId = mpqSession.SessionId;
                qHdr.FormCode = Convert.ToInt32(Quote.GetQuote(mpqSession.SessionId).FormNumber);

                var businessLogic = (MPQBusinessLogic)BusinessLogic.Factory.BusinessLogic.Get(qHdr, qData);

                businessLogic.GetLatestData();


                retv.quoteHeader = businessLogic.QHeader;
                retv.quoteData = businessLogic.QData;
                retv.IsDNQ = businessLogic.IsDnq;
                retv.IsInelligible = businessLogic.IsIneligible;
                retv.IsContactCustomerCare = businessLogic.IsContactCustomerCare;
                retv.ZipError = businessLogic.Workflow.ZipValidationError;
                retv.AddressValidationError = businessLogic.Workflow.AddressValidationError;
                retv.MPQNumber = mpqSession.MPQNumber;
                retv.EntranceKey = key;
                retv.ServiceHost = mpqSession.ServiceHost;


                if (businessLogic.LockedFields != null)
                {
                    retv.LockedFields = businessLogic.LockedFields;
                }

                var people = MPQPeople.GetMPQPeople(businessLogic.QHeader.SessionId);
                var insuredList = people.Select(person => new NamedInsured
                      {
                          FirstName = person.FirstName,
                          LastName = person.LastName,
                          DOB = DateTime.ParseExact(person.DateOfBirth, "yyyyMMdd", null),
                          SSN = (person.SocialSecurityNumber ?? " "),
                          ID = person.PgrPersonId,
                          Type = person.PrimaryAuto ? NamedInsuredType.Primary : NamedInsuredType.Additional,
                          Email = person.Email,
                          HomesiteID = person.PersonId
                      }).ToList();
                retv.NamedInsured = insuredList;

                retv.RedirectTo = new Uri(mpqSession.RedirectTo);
                if ((mpqSession.IsExpired == null || mpqSession.IsExpired == true) || (mpqSession.ExpirationTime == null || DateTime.Now > mpqSession.ExpirationTime))
                {
                    retv.EntranceStep = MPQ_PAGES.ExpiredUrl;
                }
                else
                {
                    retv.EntranceStep = (MPQ_PAGES)mpqSession.EntranceStep;
                }
                mpqSession.IsExpired = true;
                mpqSession.Save();
            
            return retv;
        }

        public NamedInsured[] GetNamedInsuredList(int sessionId)
        {
            var ppl = MPQPeople.GetMPQPeople(sessionId);
            var niArray = new NamedInsured[ppl.Count];
            int offset = 0;
            foreach (var ni in ppl.Select(p => new NamedInsured
                                                   {
                                                       FirstName = p.FirstName,
                                                       LastName = p.LastName,
                                                       DOB =
                                                           DateTime.ParseExact(p.DateOfBirth, "yyyyMMdd",
                                                                               CultureInfo.InvariantCulture),
                                                       SSN = p.SocialSecurityNumber,
                                                       Email = p.Email,
                                                       HomesiteID = p.PersonId
                                                   }))
            {
                niArray[offset] = ni;
                offset += 1;
            }

            return niArray;
        }


        public NamedInsured AddInsured(int sessionId, string fn, string ln, string dob, string ssn, string email)
        {
            string dt = DateTime.Parse(dob).ToString("yyyyMMdd");

            // add the new insured
            var newPerson = new MPQPerson
                                {
                                    SessionId = sessionId,
                                    FirstName = fn,
                                    LastName = ln,
                                    SocialSecurityNumber = ssn,
                                    Email = email,
                                    DateOfBirth = dt
                                };

            newPerson.Save();

            // refresh to get the person id
            newPerson = MPQPeople.GetMPQPeople(sessionId)
                .Where(p => String.IsNullOrEmpty(p.PgrPersonId))
                .OrderBy(p => p.SessionId).Last();

            // send back a named insured.
            var ni = new NamedInsured
                         {
                             HomesiteID = newPerson.PersonId,
                             FirstName = newPerson.FirstName,
                             LastName = newPerson.LastName,
                             DOB =
                                 DateTime.ParseExact(newPerson.DateOfBirth, "yyyyMMdd",
                                                     CultureInfo.InvariantCulture),
                             SSN = newPerson.SocialSecurityNumber,
                             Email = newPerson.Email
                         };

            return ni;
        }

        public NamedInsured EditNamedInsured(int sessionId, int personId, string fn, string ln, string dob, string ssn,
                                             string email)
        {
            MPQPerson editPerson = MPQPeople.GetMPQPeople(sessionId).First(p => p.PersonId == personId);
            editPerson.FirstName = fn;
            editPerson.LastName = ln;

            // In the QuotePresenter, SSN and DOB are sent as yes/no to client.
            // Yes values are turned into xxx-xx-xxxx and xx/xx/xxxx, respectively. 
            // Here, we ignore these as special values meaning: "not changed".
            if (dob != "xx/xx/xxxx")
            {
                DateTime dt = DateTime.Parse(dob);
                editPerson.DateOfBirth = dt.ToString("yyyyMMdd");
            }

            editPerson.Email = email;
            if (ssn != "xxx-xx-xxxx")
            {
                editPerson.SocialSecurityNumber = ssn;
            }
            editPerson.Save();

            // send back a named insured.
            var ni = new NamedInsured
                         {
                             HomesiteID = editPerson.PersonId,
                             FirstName = editPerson.FirstName,
                             LastName = editPerson.LastName,
                             DOB =
                                 DateTime.ParseExact(editPerson.DateOfBirth, "yyyyMMdd",
                                                     CultureInfo.InvariantCulture),
                             SSN = editPerson.SocialSecurityNumber,
                             Email = editPerson.Email
                         };

            return ni;
        }


        /// <summary>
        ///   For a good time, call DataFill.
        /// </summary>
        public MPQDataFillSvcResponse DataFill(MPQDataFillSvcRequest request)
        {

#if DEBUG
            if (request.MailingAddress1 == "1 Exception")
            {
                throw new Exception("Test Exception");
            }
#endif
            var response = new MPQDataFillSvcResponse();
            bool retrieved = false;

            if (DirectWebDAC.IsMPQQuote(request.MPQQuoteNumber))
            {
                int mpqId = DirectWebDAC.GetMpqIdFromMpqNumber(request.MPQQuoteNumber);

                MPQSession session = MPQSession.GetMPQSession(mpqId);
                //session.EcommerceServiceLogId = request.ECommerceServiceLogId;

                if (String.IsNullOrEmpty(session.QuoteNumber))
                    CheckQuoteTableForQuoteNumber(session);

                if (!String.IsNullOrEmpty(session.QuoteNumber))
                {
                    response = RetrieveQuote(request, mpqId, session);
                    retrieved = true;
                }
            }

            if (!retrieved)
                response = NewQuote(request);

            ECommerceMPQServiceLogger.GetInstance.LogServiceCall(response.SessionId, request.ECommerceServiceLogId);

            return response;
        }

        private static void CheckQuoteTableForQuoteNumber(MPQSession session)
        {
            Quote quote = Quote.GetQuote(session.SessionId);
            if (!String.IsNullOrEmpty(quote.CompanysQuoteNumber))
            {
                session.QuoteNumber = quote.CompanysQuoteNumber;
                session.Save();
            }
        }

        /// <summary>
        /// Web service to expose all the data entered by the user
        /// </summary>
        /// <param name="mpqNumber"></param>
        /// <returns></returns>
        public MPQCREDataSvcResponse CREData(int mpqNumber)
        {
            var response = new MPQCREDataSvcResponse(); 

            try
            {
                response.status = MPQResponseStatus.OK;

                int? sessionId = DirectWebDAC.GetSessionIdFromMpqNumber(mpqNumber);

                // We need to let async saves finish.
                var qhdr = new QuoteHeader
                {
                    SessionId = sessionId ?? -1
                };

                var mpqBusinessLogic = (MPQBusinessLogic)MPQBusinessLogicCache.StaticCollection.GetSessionBusinessLogic(qhdr);

                IBusinessLogicVisitable businessLogic = mpqBusinessLogic.BusinessLogic();

                //Refreshing Fire, Floor and wall types
                businessLogic.FireplaceTypes = null;
                businessLogic.FloorTypes = null;
                businessLogic.WallTypes = null;

                mpqBusinessLogic.FinishSave();
                mpqBusinessLogic.RefreshData();




                //IBusinessLogicVisitable mpqBusinessLogic =
                //    ((MPQBusinessLogic)MPQBusinessLogicCache.StaticCollection.GetSessionBusinessLogic(qhdr)).BusinessLogic();                

                //var qData = new QuoteData();

              // MPQBusinessLogic businessLogic = (MPQBusinessLogic)BusinessLogic.Factory.BusinessLogic.Get(qhdr, qData);
                

              

                var quote = businessLogic.Quote;
                var quoteData = businessLogic.QData ?? new QuoteData();
                var qHeader = businessLogic.QHeader;

                var structure = businessLogic.Structure;
                var coverage = businessLogic.Coverage;
                var additionalCoverage = businessLogic.AdditionalCoverage;
                var addressBroker = businessLogic.PropertyAddressBrokerOutput;
                var credit = businessLogic.Credit;


                response.HomesiteQuoteNumber = quote.CompanysQuoteNumber;
                response.WindStorm = quoteData.cvgWindStorm;
                response.FirePlaceCount = quoteData.numberOfFireplaceTypes;
                response.DistanceToFireStation = quoteData.distanceToFireStation == YesNo.Yes ? YN.Y : YN.N;
                response.DistanceToHydrant = quoteData.distanceToHydrant == YesNo.Yes ? YN.Y : YN.N;
                response.ProductCode = quote.FormNumber;
                response.DwellingType = quoteData.typeOfResidence;
                response.OccupancyType = quoteData.propertyType;
                response.TotalPremium = qHeader.TotalPremium;
                response.PrimaryFirstName = quoteData.firstName;
                response.PrimaryLastName = quoteData.lastName;
                response.DateOfBirth = VerifyDate(quoteData.dateOfBirth);

                response.SecondaryInsuredFirstName = quoteData.secondaryInsuredFirstName;

                response.SecondaryDOB = VerifyDate(quoteData.secondaryDOB);

                response.PropertyAddressLine1 = quoteData.propertyAddressLine1;
                response.PropertyAddressLine2 = quoteData.propertyAddressLine2;
                response.PropertyCity = quoteData.propertyCity;
                response.PropertyState = quoteData.propertyState;
                response.PropertyZip = quoteData.propertyZip;
                response.PropertyCounty = businessLogic.PropertyAddress.County;
                response.OriginalQuoteDate = quote.OriginalQuotationDate;
                response.DwellingCoverage = coverage.CovAAmount;
                response.OtherStructuresCoverage = coverage.CovBAmount;
                response.LossOfUseCoverage = coverage.CovDAmount;
                response.PersonalLiabilityCoverage = coverage.PersonalLiabilityLimit;
                response.MedicalCoverages = coverage.MedicalPaymentLimit;
                if (!string.IsNullOrEmpty(coverage.Deductible))
                {
                    response.Deductible = coverage.Deductible.Remove(0, 1);
                }
                response.ReplacementCostOnContentsCoverage = quoteData.cvgReplacementCostOnPersonalProperty == YesNo.Yes ? YN.Y : YN.N;
                response.IncreasedCoverageA = CalculateIncreasedCoverageAPercentage(additionalCoverage.AddLimitsLiability);
                response.EarthquakeEndorsement = quoteData.cvgIncludeEarthquake == YesNo.Yes ? YN.Y : YN.N;

                if (additionalCoverage.HH0015Flag == 1)
                {
                    response.SpecialComputerEndorsement = YN.Y;
                }
                else
                {
                    response.SpecialComputerEndorsement = quoteData.cvgIncludeComputers == YesNo.Yes ? YN.Y : YN.N;
                }
                
                response.IdentityFraudEndorsement = quoteData.cvgIncludeIdentityFraudExpenseCoverage == YesNo.Yes ? YN.Y : YN.N;
                response.MineSubsidenceEndorsement = quoteData.cvgMineSubsidence == YesNo.Yes ? YN.Y : YN.N;
                response.OpenPerilEndorsement = additionalCoverage.HH0015Flag == 1 ? YN.Y : YN.N;
                response.ScheduledPersonalPropertyFlag = additionalCoverage.SPPFlag == true ? YN.Y : YN.N;
                response.PersonalInjuryEndorsement = additionalCoverage.PersonalInjury == true ? YN.Y : YN.N; 
                response.WaterSewerBackupEndorsement = additionalCoverage.WaterBackupLimit;

                response.Territory = addressBroker.HOTerritory;
                response.NewHomePurchase = quoteData.newHomeIndicator == YesNo.Yes ? YN.Y : YN.N;
                response.CurrentInsuranceIndicator = quoteData.currentInsuranceIndicator == YesNo.Yes ? YN.Y : YN.N;

                // this could have been done by checking if the either the current or last policy expiration has a value, otherwise it is null... However, this is easier to read and understand. 
                if ((quoteData.newHomeIndicator == YesNo.No) && (quoteData.currentInsuranceIndicator == YesNo.Yes))
                {
                    response.PolicyExpirationDate = VerifyDate(quoteData.currentPolicyExpire);
                }
                else if((quoteData.newHomeIndicator == YesNo.No) && (quoteData.currentInsuranceIndicator == YesNo.No))
                {
                    response.PolicyExpirationDate = VerifyDate(quoteData.lastPolicyExpire);
                }

                response.PreviousClaims = quoteData.claimsIndicator.Equals("1") ? YN.Y : YN.N;
                response.CancellationCount = quoteData.cancelledForNonPay;


                response.EffectiveDateOfPolicy = VerifyDate(quoteData.effectiveDateOfPolicy);

                response.YearBuilt = quoteData.yearOfConstruction;
                response.SquareFootage = quoteData.livingSquareFootage;
                response.MarketValue = quoteData.approxFairMarketValue;
                response.ArchitecturalStyle = quoteData.styleOfHome;
                response.NumberOfStories = CalculateHomesiteNumOfStories(structure.NumberOfStoriesInDwelling);
                response.SeparateLivingUnits = structure.NumberOfFamilies;
                response.NumberOfFullBathrooms = quoteData.fullBathStandard;
                response.NumberOfHalfBathrooms = quoteData.halfBathStandard;
                response.WiringImprovementYear = quoteData.wiringRemodeledYear;
                response.ExteriorSiding = quoteData.constructionClass;
                response.RoofingMaterial = quoteData.roofType;
                response.RoofImprovementYear = quoteData.roofRemodeledYear;
                response.GarageSize = quoteData.garageSizeMPQ;
                response.HeatingType = structure.HeatSourceCodePrimary;
                response.WoodOrCoalBurningStove = structure.WoodStoveInd == true ? YN.Y : YN.N;
                response.IndoorSprinklerSystem = quoteData.indoorSprinklerSystem == true ? YN.Y : YN.N;
                response.DeadBoltLockIndicator = quoteData.deadboltLocks2 == YesNo.Yes ? YN.Y : YN.N;
                response.SmokeDetectorType = credit.ProtectionalDeviceCodeSmoke == 1 ? YN.Y : YN.N;
                response.HouseHoldMembers = structure.NumResidentsInHousehold;
                response.HowManyFamilies = structure.NumberOfFamilies;
                response.HowManyBoarders = structure.NoOfRoomersBoarders;
                response.BusinessCustomers = structure.BusinessMonthlyCustomers;
                response.ResidentSmokers = structure.NoOfSmokers;
                response.OwnAnyDogs = quoteData.dogsOrPets == YesNo.Yes ? YN.Y : YN.N;
                response.UnitsInBuilding = structure.NumberOfUnits;
                response.InHomeDayCare = quoteData.registeredDayCare == YesNo.Yes ? YN.Y : YN.N;
                response.DwellingReplacementCost = coverage.EstimatedReplCostAmt;
                response.CeilingHeight = CalculateCeilingHeight(structure.InteriorWallHeight8ft);
                response.CrownMoldingRooms = structure.RoomsWithCrownMolding;
                response.CathedralCeilings = structure.RoomswithCathedralCeilings;
                response.PropertyOnSlope = structure.PropertySlope == 1 ? YN.Y : YN.N;
                response.PoolIndicator = quoteData.swimmingPool == true ? YN.Y : YN.N;
                response.PoolEnclosedFence = quoteData.swimmingPoolFence == YesNo.Yes ? YN.Y : YN.N;
                response.Trampoline = structure.TrampolineInd == 1 ? YN.Y : YN.N;
                response.CentralAirCondition = quoteData.centralAir == YesNo.Yes ? YN.Y : YN.N;
                response.DogsEverBite = structure.DogBitingHistory == true ? YN.Y : YN.N;
                response.DogBreed = structure.DogTypeCd;
                response.LandLeasedToThirdParty = quoteData.leasedToThirdParty == true ? YN.Y : YN.N;
                response.FarmingOnProperty = quoteData.farming == true ? YN.Y : YN.N;
                response.ContentsCoverage = coverage.CovCAmount + coverage.IncreasedCovCAmt;
                if (!string.IsNullOrEmpty(quoteData.wiringOnCircuitBreakers))
                {
                    response.ElectricWiringType = GetStaticDataSourceDisplayText("WiringOnCircuitBreakers",
                                                                                 quoteData.wiringOnCircuitBreakers);
                }
                response.WillPropertyBeRented = quoteData.rentedOrVacant == YesNo.Yes ? YN.Y : YN.N;
                response.NameOnDeed = quoteData.estate == true ? YN.Y : YN.N;
                response.DwellingUse = quoteData.occupancyType;

               #region Wind Hail Deductible

                response.WindHailDeductible = new WindHail();
                CalculateWindHail(response, coverage);

                #endregion

                #region Translation
                var lookupProvider = new LookupProvider();

                #region Recalculated Data  

                //UW Company
                int UWCompanyID = DirectWebDAC.GetUWCompany(qHeader.State, QuoteServices.GetAMFAccountNumberByPartnerID(qHeader.PartnerId), DateTime.Today,qHeader.FormCode);

                //Keytown has the string value of protection class for states "GA", "KS", "MS", "NC", it is retrieved at ProtectionBroker.cs in ServiceBroker, check the end of ProtectionBroker.GetProtectionCodeInternal()
                CREReCalculatedData(response, (int)businessLogic.PropertyAddress.DistanceToHydrant, (int)businessLogic.PropertyAddress.DistanceToFireStation, lookupProvider, addressBroker.FireProtectionClassCd ?? string.Empty, qHeader.State, qHeader.RatingVersion, "A", UWCompanyID, quoteData.constructionClass, qHeader.FormCode, businessLogic.PropertyAddress.KeyTown);
                #endregion

                //Roof shape
                response.RoofShape = CalculateRoofShape(quoteData.roofShape, lookupProvider);

                //Foundation Type
                response.FoundationType = CalculateFoundationType(quoteData.foundationType);

                //Foundation Shape
                response.FoundationShape = CalculateFoundationShapeType(quoteData.foundationShape, lookupProvider);

                //FirePlace Types
                response.FirePlaceList = CalculateFirePlaceType(quoteData, lookupProvider,businessLogic.FireplaceTypes.Count);

                //FloorType
                response.FloorTypes = CalculateFloorType(quoteData, lookupProvider, businessLogic.FloorTypes.Count);

                //WallType
                response.WallTypes = CalculateWallType(quoteData, lookupProvider, businessLogic.WallTypes.Count);

                //Garage Types
                response.GarageType = CalculateGarageType(quoteData);

                //Portion of Basement
                response.PortionOfFinishedBasement = CalculatePortionofFinishedBasement(structure);

                //Alarms
                if (!string.IsNullOrEmpty(quoteData.burglarAlarm))
                {
                    response.BurglarAlarmType = GetStaticDataSourceDisplayText("BurglarAlarm", quoteData.burglarAlarm);
                }
                if (!string.IsNullOrEmpty(quoteData.fireAlarm))
                {
                    response.FireAlarmType = GetStaticDataSourceDisplayText("FireAlarm", quoteData.fireAlarm);
                }

                //Kitchen
                response.KitchenCountertopMaterial = CalculateKitchenCounterMaterial(quoteData.kitchenCountertopMaterial, lookupProvider);

                //Oil Tank

                if (!string.IsNullOrEmpty(structure.OilStorageTankLocationCd))
                {
                    if (string.Equals(structure.OilStorageTankLocationCd, "0"))
                    {
                        response.OilTankLocation = "None";
                    }
                    else
                        response.OilTankLocation = GetStaticDataSourceDisplayText("OilTankExtended", structure.OilStorageTankLocationCd);
                }

                //Above Pool Indicator
                if (!string.IsNullOrEmpty(quoteData.swimmingPoolType))
                {
                    response.AboveGroundPoolIndicator = GetStaticDataSourceDisplayText("SwimmingPoolType",
                                                                                       quoteData.swimmingPoolType);
                }


                #endregion


            }
            catch (Exception ex)
            {
                ECommerceMPQServiceLogger.GetInstance.LogServiceException(ex, "CREData", mpqNumber);
                response.status = MPQResponseStatus.Error;
                response.ErrorMsg = "An error has occurred while retrieving the CRE data for MPQ#: " +
                                    mpqNumber.ToString();
            }
            return response;
        }

        private string GetStaticDataSourceDisplayText(string controlName, string value)
        {
            var xEle = ServiceLibraryCache.StaticCollection.StaticDataSourcesDOM;

            var itemXE =
                from c in xEle.Elements("DataSource").Descendants()
                where (string)c.Parent.Parent.Attribute("Name") == controlName && c.Attribute("Value").Value == value
                select c;

            return itemXE.First().Value;
        }



        private void CREReCalculatedData(MPQCREDataSvcResponse response, int distanceToHydrant, int distanceToFireStation, LookupProvider lookupProvider, string fireProtectionClassCd, string stateCode, int ratingVersion, string premiumType, int UWCompanyID, string constructionClassType, int formNumber, string protectionClassCode)
        {
            DataTable dtData = lookupProvider.GetCREData(distanceToHydrant,
                                                         distanceToFireStation,
                                                         fireProtectionClassCd, stateCode, ratingVersion,
                                                         premiumType, UWCompanyID, constructionClassType, formNumber, protectionClassCode);

            if (dtData.Rows.Count > 0)
            {
                //Get the culture property of the thread.
                CultureInfo cultureInfo = Thread.CurrentThread.CurrentCulture;
                //Create TextInfo object.
                TextInfo textInfo = cultureInfo.TextInfo;

                response.ProtectionClass = dtData.Rows[0]["ProtectionClass"].ToString();
                response.ConstructionClass = dtData.Rows[0]["ConstructionClass"].ToString().Trim();
                response.HomesiteUnderwritingCo = textInfo.ToTitleCase(dtData.Rows[0]["UnderwrittingCompany"].ToString().ToLower().Trim());
            }
        }

       
        private int CalculateCeilingHeight(int? height)
        {
            var ceilingHeight = 0;
            switch (height)
            {
                case 1:
                    ceilingHeight = 8;
                    break;
                case 2:
                    ceilingHeight = 9;
                    break;
                case 3:
                    ceilingHeight = 10;
                    break;
                default:
                    ceilingHeight = 0;
                    break;
            }
            return ceilingHeight;

        }

        private double CalculateHomesiteNumOfStories(string numberOfStories)
        {
            double numberOfStoriesInDwelling = 1;

            switch (numberOfStories)
            {
                case "2": numberOfStoriesInDwelling = 1.5;
                    break;
                case "3": numberOfStoriesInDwelling = 2;
                    break;

                case "4": numberOfStoriesInDwelling = 2.5;
                    break;

                case "5": numberOfStoriesInDwelling = 3;
                    break;

                case "6": numberOfStoriesInDwelling = 3.5;
                    break;

                case "7": numberOfStoriesInDwelling = 4;
                    break;
                default: numberOfStoriesInDwelling = 1;
                    break;
            }

            return numberOfStoriesInDwelling;
        }
        private int? CalculatePortionofFinishedBasement(Structure structure)
        {
            int? portionOfFinishedBasement = 0;

            int finishedBasementSqFt = 0;
            if (structure.FinishedBasementSqFt.HasValue)
            {
                finishedBasementSqFt = structure.FinishedBasementSqFt.Value;
            }

            int livingSquareFootage = -1; //This field is mandatory in the UI
            if (structure.SquareFootage.HasValue)
            {
                livingSquareFootage = structure.SquareFootage.Value;
            }

            if (finishedBasementSqFt != 0)
            {
                double ratio =
                    (BusinessLogicSessionDataBinder.GetTRSNumOfStories(int.Parse(structure.NumberOfStoriesInDwelling)) *
                     finishedBasementSqFt) / livingSquareFootage;

                if (ratio <= 0.25)
                {
                    portionOfFinishedBasement = 25;
                }
                else if (ratio > 0.25 && ratio <= 0.5)
                {
                    portionOfFinishedBasement = 50;
                }
                else if (ratio > 0.5 && ratio <= 0.75)
                {
                    portionOfFinishedBasement = 75;
                }
                else if (ratio > 0.75)
                {
                    portionOfFinishedBasement = 100;
                }
            }
            else
            {
                portionOfFinishedBasement = 0;
            }

            return portionOfFinishedBasement;
        }

        private string CalculateRoofShape(string roofShapeID,
                                               LookupProvider lookupProvider)
        {
            string roofShape = string.Empty;
            DataTable dvRoofShape = lookupProvider.GetRoofShapeTypes().ToTable();
            foreach (DataRow row in dvRoofShape.Rows)
            {
                if (row["RoofShapeId"].ToString() == roofShapeID)
                {
                    roofShape = row["DisplayText"].ToString();
                }
            }

            return roofShape;
        }

        private string CalculateKitchenCounterMaterial(string kitchenCounterMaterialID,
                                               LookupProvider lookupProvider)
        {
            string kitchenCounterMaterial = string.Empty;
            DataTable dvKitchenCounterMaterial = lookupProvider.GetCounterTopMaterialTypes().ToTable();
            foreach (DataRow row in dvKitchenCounterMaterial.Rows)
            {
                if (row["CountertopMaterialId"].ToString() == kitchenCounterMaterialID)
                {
                    kitchenCounterMaterial = row["DisplayText"].ToString();
                }
            }

            return kitchenCounterMaterial;
        }


        private List<string> CalculateFirePlaceType(QuoteData quoteData, LookupProvider lookupProvider, int firePlaceCount)
        {
            List<string> firePlaceList = new List<string>();

            DataView dvFirePlaceType = lookupProvider.GetFirePlaceTypes();

            for (int i = firePlaceCount; i >= 1; i--)
            {
                object fireval = typeof (QuoteData).GetProperty(string.Concat("fireplaceType", i)).GetValue(quoteData,null);

                if (fireval != null)
                {
                    dvFirePlaceType.RowFilter = string.Concat("FirePlacesTypeID = ", fireval);

                    if (dvFirePlaceType[0] != null)
                    {
                        firePlaceList.Add(dvFirePlaceType[0]["DisplayText"].ToString());
                    }
                }
            }

            return firePlaceList; 
        }

        private List<FloorTypeDisplay> CalculateFloorType(QuoteData quoteData, LookupProvider lookupProvider, int floorTypesCount)
        {
            List<FloorTypeDisplay> floorTypeDisplayList = new List<FloorTypeDisplay>();
            DataView dvFloorType = lookupProvider.GetFloorCoverings();


            for (int i = floorTypesCount; i >= 1; i--)
            {
                object floorval = typeof(QuoteData).GetProperty(string.Concat("floorTypeDisplayIndex", i)).GetValue(quoteData, null);
                object floorPercentage = typeof(QuoteData).GetProperty(string.Concat("floorTypePercentage", i)).GetValue(quoteData, null);

                if (floorval != null)
                {
                    dvFloorType.RowFilter = string.Concat("FloorCoveringsTypeId = ", floorval);

                    if (dvFloorType[0] != null)
                    {
                        FloorTypeDisplay floorType = new FloorTypeDisplay
                                                         {
                                                             FloorTypeName = dvFloorType[0]["DisplayText"].ToString(),
                                                             Percentage = floorPercentage.ToString()
                                                         };
                        floorTypeDisplayList.Add(floorType);
                    }
                }
            }
            return floorTypeDisplayList;
        }

        private List<WallTypeDisplay> CalculateWallType(QuoteData quoteData,
                                                  LookupProvider lookupProvider, int wallTypesCount)
        {
            List<WallTypeDisplay> wallTypeDisplay = new List<WallTypeDisplay>();
            DataView dvWallType = lookupProvider.GetInteriorWallTypes();

            for (int i = wallTypesCount; i >= 1; i--)
            {
                object wallval = typeof(QuoteData).GetProperty(string.Concat("wallTypeDisplayIndex", i)).GetValue(quoteData, null);
                object wallPercentage = typeof(QuoteData).GetProperty(string.Concat("wallTypePercentage", i)).GetValue(quoteData, null);

                if (wallval != null)
                {
                    dvWallType.RowFilter = string.Concat("InteriorWallTypesId = ", wallval);

                    if (dvWallType[0] != null)
                    {
                        var wallType = new WallTypeDisplay
                                           {
                                               WallTypeName = dvWallType[0]["DisplayText"].ToString(),
                                               Percentage = wallPercentage.ToString()
                                           };
                        wallTypeDisplay.Add(wallType);
                    }
                }
            }
            return wallTypeDisplay;
        }


        private string CalculateFoundationShapeType(string foundationShape, LookupProvider lookupProvider)
        {
            DataView dvFoundationType = lookupProvider.GetFoundationShapeTypes();
            string foundationShapeName = string.Empty;

            if (string.IsNullOrEmpty(foundationShape) == false)
            {
                dvFoundationType.RowFilter = string.Concat("FoundationShapeId = ", foundationShape);

                if (dvFoundationType[0] != null)
                {
                    foundationShapeName = dvFoundationType[0]["DisplayText"].ToString();
                }
            }
            return foundationShapeName;
        }

        private string CalculateGarageType(QuoteData quoteData)
        {
            string garageType = null;
            switch (quoteData.garageTypeMPQ)
            {
                case "2":
                    garageType = "Attached";
                    break;
                case "3":
                    garageType = "Detached";
                    break;
                default: //"1"
                    garageType = "No Garage";
                    break;
            }
            return garageType;
        }

       private string CalculateFoundationType(string foundationTypeID)
        {
            string foundationType = null;

            switch (foundationTypeID)
            {
                case "1":
                    foundationType = "Closed Basement";
                    break;
                case "2":
                    foundationType = "Walk-In";
                    break;
                case "3":
                    foundationType = "Slab";
                    break;
                case "4":
                    foundationType = "Stilts";
                    break;
                case "5":
                    foundationType = "Crawlspace";
                    break;
                default: //"0"
                    foundationType = "None";
                    break;
            }
            return foundationType;
        }

       private void CalculateWindHail(MPQCREDataSvcResponse response, Coverage coverage)
        {
            if (!string.IsNullOrEmpty(coverage.HurricaneDeductible))
            {
                if (coverage.HurricaneDeductible.Contains("%"))
                {
                    response.WindHailDeductible.Amount =
                        (Convert.ToDecimal(coverage.HurricaneDeductible.Replace("%", string.Empty)) / 100) *
                        Convert.ToDecimal(coverage.CovAAmount);
                    response.WindHailDeductible.Percentage =
                        Convert.ToDecimal(coverage.HurricaneDeductible.Replace("%", string.Empty));
                }
                else
                {
                    response.WindHailDeductible.Amount =
                        Convert.ToDecimal(coverage.HurricaneDeductible.Replace("$", string.Empty));
                }
            }
        }

        private DateTime? VerifyDate(DateTime? date)
        {
            DateTime defaultDate = DateTime.MinValue;
            if (date != defaultDate)
            {
                return date;
            }
            return null;
        }

        private int CalculateIncreasedCoverageAPercentage(short? number)
        {
            int increasedCoverageAPercentage = 0;
            switch (number)
            {
                case 3:
                    increasedCoverageAPercentage = 25;
                    break;

                case 2: 
                    increasedCoverageAPercentage = 50;
                    break;
                default:
                    increasedCoverageAPercentage = 0;
                    break;
            }
            return increasedCoverageAPercentage;
        }

        /// <summary>
        /// Provides a dictionary of token strings that can be found in external DNQ messages and their
        /// corresponding Xml element's that contain the value with which to replace it from the Info section
        /// of the partner Xml.
        /// </summary>
        /// <remarks>
        /// These items are in a particular order (most importantly, PGR_MESSAGE_TOKEN is first)
        /// because the tokens they replace may include tokens that are listed further down the list.
        /// </remarks>
        private Dictionary<string, string> _dnqMessageTokens = new Dictionary<string, string>
        {
            { "PGR_MESSAGE_TOKEN", "DNQMessageAppendix1" },
            { "PHONE_NUMBER", "PhoneNumber" },
            { "DNQ_PH_NUMBER", "DNQMessagePhoneNumber" },
        };

        /// <summary>
        /// Created as part of ITR 7425, formats the given external DNQ message by replacing tokens contained in the string
        /// with their corresponding values from the Info section of the partner Xml.
        /// </summary>
        /// <param name="dnqExistingMessage">The message to format.</param>
        /// <returns>The formatted message.</returns>
        public string AppendPartnerSpecificInfoToExistingDNQMessage(string dnqExistingMessage)
        {
            var output = new StringBuilder();

            if (dnqExistingMessage != null)
            {
                var partnerConfig = IQuoteConfiguration.GetConfigurationInstance();

                if (partnerConfig != null)
                {
                    var trimmedMessage = dnqExistingMessage.Trim();

                    if (trimmedMessage.Any())
                    {
                        output.Append(trimmedMessage);

                        foreach (var token in _dnqMessageTokens)
                        {
                            output.Replace(token.Key, partnerConfig.Info(token.Value));
                        }
                    }
                }
            }

            return output.ToString();
        }

        public MPQQuoteInfoSvcResponse QuoteInfo(MPQQuoteInfoSvcRequest request)
        {
            var response = new MPQQuoteInfoSvcResponse();
            int? sessionId = DirectWebDAC.GetSessionIdFromMpqNumber(request.MPQQuoteNumber);
            

            // We need to let async saves finish.
            var qhdr = new QuoteHeader
                           {
                               SessionId = sessionId ?? -1
                           };
            // We need a switch here for BL
            int experienceFlag = DirectWebDAC.GetExperienceFlagFromSessionId(qhdr.SessionId);
            // Get ExperienceFlag from MPQSessionMaster
            MPQBusinessLogic mpqBusinessLogic;
            if (experienceFlag == 1)
            {
                mpqBusinessLogic = (MPQBusinessLogic)MPQBusinessLogicCache.StaticCollection.GetSessionBusinessLogic(qhdr);
            }
            else
            {
                mpqBusinessLogic = new MPQBusinessLogic(qhdr.SessionId);
            }
            
#if DEBUG
            if (mpqBusinessLogic.BusinessLogic().PropertyAddress.Addr2 == "QuoteInfo Exception")
            {
                throw new Exception("Test Exception");
            }
#endif
            mpqBusinessLogic.FinishSave();
           
            string message = String.Format("[QuoteInfo] Server: {0}", Environment.MachineName);

            ThreadLog.ForensicLog(message,
                                  sessionId ?? 0,
                                  request.MPQQuoteNumber,
                                  mpqBusinessLogic.MpqSession.EntranceKey.ToString());

            if (((mpqBusinessLogic.BusinessLogic().Quote.QuoteStatus == 4) // 4 is Early Cash successful and also indicates purchased policy while Batch is running
                || (mpqBusinessLogic.BusinessLogic().Quote.QuoteStatus == 6)) // ITR#9394 Adding this. Quote Status of 6 means successful purchase
                && (String.IsNullOrEmpty(mpqBusinessLogic.BusinessLogic().Quote.CompanysPolicyNumber) == false))
            {
                response.status = MPQResponseStatus.Purchased;
                response.companyPolicyNumber = mpqBusinessLogic.BusinessLogic().Quote.CompanysPolicyNumber;
            }
            else
            {
                DirectWebDAC.StatusFlags statusFlag = DirectWebDAC.GetDNQsFromMpqNumber(request.MPQQuoteNumber);
                Header header = Header.GetHeader(qhdr.SessionId);
                int mpqId = mpqBusinessLogic.MPQSessionMasterId <= 0
                                ? DirectWebDAC.GetMpqIdFromSessionId(qhdr.SessionId)
                                : (mpqBusinessLogic).MPQSessionMasterId;

                MPQSession mpqSession = MPQSession.GetMPQSession(mpqId);
                response.formCode = mpqBusinessLogic.QHeader.FormCode;
                response.CompanysQuoteNumber = mpqBusinessLogic.BusinessLogic().Quote.CompanysQuoteNumber;
                //ITR#8945
                if (statusFlag == DirectWebDAC.StatusFlags.OK && mpqBusinessLogic.BusinessLogic().IsContactCustomerCare)
                {
                    statusFlag = DirectWebDAC.StatusFlags.ContactCustomerCare;
                }
                switch (statusFlag)
                {
                    case DirectWebDAC.StatusFlags.DNQ:
                        if (mpqBusinessLogic.MpqSession.GamingDnq)
                        {
                            response.status = MPQResponseStatus.Ineligible;
                            response.statusMessage =
                                "Our records indicate you have applied for a quote in the past and you are not eligible to re-quote. We apologize for the inconvenience.";
                        }
                        else
                        {
                            response.status = MPQResponseStatus.DNQ;

                            string internalDnqReason = mpqBusinessLogic.BusinessLogic().Quote.DNQReasons;

                            var dnqMessage = DirectWebDAC.getExternalMPQDNQReason(internalDnqReason, header.State);

                            //ITR-7425
                            response.statusMessage = AppendPartnerSpecificInfoToExistingDNQMessage(dnqMessage);
                        }

                        break;
                    case DirectWebDAC.StatusFlags.Error:
                        response.status = MPQResponseStatus.Error;
						//ITR#5599
						// Quoteinfo call returning Error status
						LogPublishClient.LogEvent(mpqBusinessLogic.QHeader, 7);
                        break;
                    case DirectWebDAC.StatusFlags.Ineligible:
                        response.status = MPQResponseStatus.Ineligible;
                        response.statusMessage = "We're sorry, we're unable to provide an online quote today.";
                        break;
                    case DirectWebDAC.StatusFlags.Incomplete:
                        response.status = MPQResponseStatus.Incomplete;
                        break;
                    case DirectWebDAC.StatusFlags.NotQuoted:
                        response.status = MPQResponseStatus.NotQuoted;
                        break;
                    case DirectWebDAC.StatusFlags.ContactCustomerCare:
                        //ITR#8945 - HomeBusinessEndorement
                        UIServices objUIServices = new UIServices();
                        response.status = objUIServices.UseDnqResponseStatusForCustomerCare() ? MPQResponseStatus.DNQ : MPQResponseStatus.ContactCustomerCare;
                        response.statusMessage = objUIServices.GetHomeBusinessCustomerCareMessage(mpqBusinessLogic.QHeader);
                        break;
                    default:
                        if (sessionId != null)
                        {
                            if (!String.IsNullOrEmpty(mpqBusinessLogic.BusinessLogic().Coverage.PolicyEffDate) &&
                                mpqBusinessLogic.QuoteExpired())
                            {
                                response.status = MPQResponseStatus.Expired;
                                // Make the change Here
                                string url = string.Empty;
                                switch(experienceFlag)
                                {
                                    case 1:
                                        url =
                                            CacheSet
                                                <ConfigurationCache, string,
                                                    StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Get()
                                                .RedirectUrl;
                                        break;
                                    case 2:
                                        url =
                                            CacheSet
                                                <ConfigurationCache, string,
                                                    StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Get()
                                                .DesktopRedirectUrl;
                                        break;
                                    case 3:
                                        url =
                                            CacheSet
                                                <ConfigurationCache, string,
                                                    StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Get()
                                                .MobileRedirectUrl;
                                        break;
                                    default:
                                        url =
                                            CacheSet
                                                <ConfigurationCache, string,
                                                    StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Get()
                                                .RedirectUrl;
                                        break;

                                }
                                
                                response.redirectURL = String.Format(url, mpqSession.EntranceKey);
                            }
                            else
                            {
                                response.status = MPQResponseStatus.OK;
                                response.completeQuoteFlag =
                                    Convert.ToInt32(mpqBusinessLogic.BusinessLogic().Quote.CompleteQuoteFlag ?? 0);

                                if (!String.IsNullOrEmpty(request.AutoPolicyNumber))
                                {
                                    bool rerate = String.IsNullOrEmpty(mpqBusinessLogic.BusinessLogic().Quote.AutoPolicyNumber);

                                    mpqBusinessLogic.BusinessLogic().Quote.AutoPolicyNumber = request.AutoPolicyNumber;
                                    mpqBusinessLogic.BusinessLogic().Quote.Save();

                                    if (rerate)
                                    {
                                        mpqBusinessLogic.ReRate();
                                    }
                                }
                                else if (String.IsNullOrEmpty(mpqBusinessLogic.BusinessLogic().Quote.AutoPolicyNumber))
                                {
                                    QuoteServices.ReapplyDiscount(qhdr, mpqBusinessLogic.BusinessLogic().Quote);
                                    mpqBusinessLogic.ReRate();
                                }


                                try
                                {
                                    if (mpqBusinessLogic.BusinessLogic().Quote.PolicyPremiumAmt != null)
                                    {
                                        QuoteSummary qs = null;
                                        if (mpqBusinessLogic.BusinessLogic().Header.IsApexState == true)
                                        {
                                            var quote = mpqBusinessLogic.BusinessLogic().Quote;
                                            var qsList = QuoteServices.GetQuoteSummary(mpqBusinessLogic.BusinessLogic().Header, 
                                                mpqBusinessLogic.BusinessLogic().Quote,
                                                mpqBusinessLogic.BusinessLogic().Coverage);
                                            qs = QuoteServices.GetQuoteSummaryFromList(qsList, quote.ApexBillingOption);
                                        }
                                        else
                                        {
                                             qs = QuoteServices.GetQuoteSummary(mpqBusinessLogic.BusinessLogic().Header,
                                                                                            mpqBusinessLogic.BusinessLogic().Quote,
                                                                                            mpqBusinessLogic.BusinessLogic().Coverage, 
                                                                                            mpqBusinessLogic.BusinessLogic().Structure.PropertyType,
                                                                                            true);
                                        }
                                        if (qs.Downpayment != null && qs.TotalPremium != "$0.0")
                                        {
                                            response.paymentPlanType = PaymentPlanTypes.tenPay;
                                            var temp = qs.TotalPremium.Replace("$", "");
                                            response.premiumAmount = Convert.ToDecimal((temp.Replace(",", "")));
                                            temp = qs.Downpayment.Replace("$", "");
                                            response.downPaymentAmount = Convert.ToDecimal(temp.Replace(",", ""));
                                        }
                                        
                                    }
                                }
// ReSharper disable EmptyGeneralCatchClause
                                catch
// ReSharper restore EmptyGeneralCatchClause
                                {
                                    // this is bad.
                                }

                                mpqBusinessLogic.FinishSave();
								// If quote status is incomplete, log status a bindable
								// ITR#5599
                                if ((response.completeQuoteFlag == 5) || (response.completeQuoteFlag == 0) && mpqBusinessLogic.QHeader.TotalPremium != null)
								{
									qhdr.QuoteNumber = header.QuoteNumber;
								    if (header.isRetrieve != null) qhdr.IsRetrieve = (bool)header.isRetrieve;
								    LogPublishClient.LogEvent(mpqBusinessLogic.QHeader, 2);

								}

                                // Make the change here
                                string url = string.Empty;
                                switch (experienceFlag)
                                {
                                    case 1:
                                        url =
                                            CacheSet
                                                <ConfigurationCache, string,
                                                    StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Get()
                                                .RedirectUrl;
                                        break;
                                    case 2:
                                        url =
                                            CacheSet
                                                <ConfigurationCache, string,
                                                    StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Get()
                                                .DesktopRedirectUrl;
                                        break;
                                    case 3:
                                        url =
                                            CacheSet
                                                <ConfigurationCache, string,
                                                    StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Get()
                                                .MobileRedirectUrl;
                                        break;
                                    default:
                                        url =
                                            CacheSet
                                                <ConfigurationCache, string,
                                                    StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Get()
                                                .RedirectUrl;
                                        break;

                                }

                                response.redirectURL = String.Format(url, mpqSession.EntranceKey);
                            }
                        }
                        break;
                }
            }

            ECommerceMPQServiceLogger.GetInstance.LogServiceCall(
                sessionId ?? 0, 
                request.QuoteInfoServiceLogId);

            return response;
        }
 
        public MPQDataFillSvcResponse RetrieveQuote(MPQDataFillSvcRequest newQuoteRequest, int mpqId,
                                                    MPQSession previousSession)
        {
            var mpqNumber = newQuoteRequest.MPQQuoteNumber;

            var newQuoteReponse = new MPQDataFillSvcResponse();

            if (previousSession != null)
            {
                // validate retrieve
                var addr =
                    Addresses.GetAddresses(previousSession.SessionId).First(x => x.AddressType == "PROPERTY");

                var quote = Quote.GetQuote(previousSession.SessionId);
                var header = Header.GetHeader(previousSession.SessionId);
                var originalQuoteDate = quote.OriginalQuotationDate ?? DateTime.Today;

                if ((String.IsNullOrEmpty(previousSession.PolicyNumber) &&
                    (((quote.QuoteStatus != 4) && // 4 is Early Cash successful and also indicates purchased policy while Batch is running
                    (quote.QuoteStatus != 6)) || // ITR#9394 Adding this. Quote Status of 6 means successful purchase   
                     String.IsNullOrEmpty(quote.CompanysPolicyNumber)) &&
                     !String.IsNullOrEmpty(previousSession.QuoteNumber)) ||
                    previousSession.GamingDnq || previousSession.MoratoriumDnq)
                {
                    if (previousSession.MoratoriumDnq)
                    {
                        return NewQuote(newQuoteRequest);
                    }

                    if (previousSession.GamingDnq && QuoteServices.BeenDaysSince(180, originalQuoteDate))
                    {
                        return NewQuote(newQuoteRequest);
                    }
                    
                    if (previousSession.OtherDnq ||
                             (previousSession.GamingDnq && !QuoteServices.BeenDaysSince(180, originalQuoteDate)))
                    {
                        newQuoteReponse.ResponseCode = MPQMessageActionState.DNQ;

                        var dnqMessage = DirectWebDAC.getExternalMPQDNQReason(quote.DNQReasons, header.State);

                        //ITR-7425
                        newQuoteReponse.ResponseMessage = AppendPartnerSpecificInfoToExistingDNQMessage(dnqMessage);

                        newQuoteReponse.EntranceKey = previousSession.EntranceKey;

                        previousSession.EntranceStep = (int) MPQ_PAGES.CombinedRatesPage;

                        previousSession.Save();

                        newQuoteReponse.RedirectURL =
                            String.Format(CacheSet<ConfigurationCache, string, StaticSingletonAllocator<ConfigurationCache>>.StaticCollection.Get().MobileRedirectUrl,
                                          newQuoteReponse.EntranceKey);
                    }
                    else
                    {
                        HorizonRetrieve(newQuoteReponse, newQuoteRequest, previousSession, addr);

                        if (_businessLogic.BusinessLogic().Quote.PolicyPremiumAmt > 0)
                        {
                            AttachPricingInfoToResponse(newQuoteReponse);
                        }
                    }
                }
                else if (String.IsNullOrEmpty(previousSession.QuoteNumber))
                {
                    throw new FaultException<RetrieveFail>(
                        new RetrieveFail("Retrieve mpq quote " + mpqNumber, "Quote number not found",
                                         previousSession.SessionId),
                        new FaultReason("Could not find a quote number associated with the previous session."));
                }
                else
                {
                    // purchased quote
                    newQuoteReponse.ResponseCode = MPQMessageActionState.IssuedPolicy;
                    newQuoteReponse.ResponseMessage = quote.CompanysPolicyNumber;
                }
            }
            else
            {
                return NewQuote(newQuoteRequest);
            }


            return newQuoteReponse;
        }

        private void AttachPricingInfoToResponse(MPQDataFillSvcResponse newQuoteReponse)
        {
            try
            {
                QuoteSummary qs = null;
                if (_businessLogic.BusinessLogic().Header.IsApexState == true)
                {
                    var quote = _businessLogic.BusinessLogic().Quote;
                    var qsList = QuoteServices.GetQuoteSummary(_businessLogic.BusinessLogic().Header, _businessLogic.BusinessLogic().Quote, 
                        _businessLogic.BusinessLogic().Coverage);
                    qs = QuoteServices.GetQuoteSummaryFromList(qsList, quote.ApexBillingOption);
                }
                else
                {
                    qs = QuoteServices.GetQuoteSummary(_businessLogic.BusinessLogic().Header,
                                                                    _businessLogic.BusinessLogic().Quote,
                                                                    _businessLogic.BusinessLogic().Coverage, 
                                                                    _businessLogic.BusinessLogic().Structure.PropertyType,
                                                                    true);
                }

                if (qs.Downpayment != null && qs.TotalPremium != "$0.0")
                {
                    newQuoteReponse.paymentPlanType = PaymentPlanTypes.tenPay;
                    string temp = qs.TotalPremium.Replace("$", "");
                    newQuoteReponse.premiumAmount = Convert.ToDecimal((temp.Replace(",", "")));
                    temp = qs.Downpayment.Replace("$", "");
                    newQuoteReponse.downPaymentAmount = Convert.ToDecimal(temp.Replace(",", ""));
                }
            }
// ReSharper disable EmptyGeneralCatchClause
            catch (Exception)
// ReSharper restore EmptyGeneralCatchClause
            {
                // any thrown exceptions in the above block basically means
                // we have no payment information to display, so just supress
                // the error and move on with our lives.
            }
        }

        private void HorizonRetrieve(MPQDataFillSvcResponse newQuoteReponse, MPQDataFillSvcRequest newQuoteRequest,
                                     MPQSession mpqSession, Address addr)
        {
            var quotePartnerIds = DirectWebDAC.GetPartnerID(mpqSession.QuoteNumber);

            
            #if DEBUG
                var debugOverride = quotePartnerIds.Contains(8);
            #endif

            #if !DEBUG
                var debugOverride = false;                    
            #endif

            if (quotePartnerIds.Contains(_mpqPid) || debugOverride)
            {
                var iso = new ISOProvider();
                string dbDateOfBirth;
                string dbZip;
                int dbForm;
                int dbQuoteCompleteFlag;
                iso.GetZipAndDOBForRetrieve(
                    mpqSession.QuoteNumber,
                    out dbZip,
                    out dbDateOfBirth,
                    out dbForm,
                    out dbQuoteCompleteFlag);


                if (dbForm != 2)
                {
                    switch (dbQuoteCompleteFlag)
                    {
                        case -3:
                        case -2:
                            throw new FaultException<RetrieveFail>(
                                new RetrieveFail("Bad data from subsystem"),
                                new FaultReason("Quote complete flag: " + dbQuoteCompleteFlag)
                                );

                        case 2:
                        case 1:
                        case 5:
                            ProcessSuccessfulRetrieve(newQuoteReponse, newQuoteRequest, mpqSession, addr, dbQuoteCompleteFlag, dbZip, dbForm, dbDateOfBirth);
                            break;
                    }
                }
                else
                {
                    throw new FaultException<RetrieveFail>(
                        new RetrieveFail("Retrieve H02"),
                        new FaultReason("ISO GetZipAndDOBForRetrieve returned:" + dbForm)
                        );
                }
            }
            else
            {
                throw new FaultException<RetrieveFail>(
                    new RetrieveFail("Bad partner id"),
                    new FaultReason("Quote not partner id " + _mpqPid)
                    );
            }
        }

        private void ProcessSuccessfulRetrieve(MPQDataFillSvcResponse newQuoteReponse, MPQDataFillSvcRequest newQuoteRequest,
                                               MPQSession mpqSession, Address addr, int dbQuoteCompleteFlag, string dbZip,
                                               int dbForm, string dbDateOfBirth)
        {
            var qHdr = SetQuoteHeaderForRetrieve(
                addr, mpqSession.QuoteNumber, dbZip, dbDateOfBirth, dbForm, dbQuoteCompleteFlag
                );

            var previousSessionId = mpqSession.SessionId;

            if (newQuoteRequest.RedirectTo != null)
                mpqSession.RedirectTo = newQuoteRequest.RedirectTo.ToString();

            var host = GetDefaultHostFromClient();

            var serviceHost = new ServiceHost
              {
                  SessionId = qHdr.SessionId,
                  Host = host
              };

            // This is the new changes for MPQRWD.
            int experience = FlashExperience;
            if (newQuoteRequest.Experience.Equals("FQ"))
            {
                experience = FQExperience;
            }
            else if (newQuoteRequest.Experience.Equals("DAQ"))
            {
                experience = DAQExperience;
            }

            serviceHost.Save();
            mpqSession.ServiceHost = host;

            mpqSession.ServiceHost = GetDefaultHostFromClient();
            mpqSession.SessionId = qHdr.SessionId;
            mpqSession.EcommerceServiceLogId = newQuoteRequest.ECommerceServiceLogId;
            mpqSession.Experience = experience;
            mpqSession.IsExpired = false;
            mpqSession.ExpirationTime = DateTime.Now.AddMinutes(GetUrlTimeout());
            mpqSession.EntranceKey = Guid.NewGuid();
            mpqSession.Save();

            _businessLogic = (MPQBusinessLogic) BusinessLogic.Factory.BusinessLogic.Get(qHdr);

            _businessLogic.BusinessLogic().PropertyAddress.StateProvCd = addr.StateProvCd;
            _businessLogic.BusinessLogic().PropertyAddress.PostalCode = addr.PostalCode;
            _businessLogic.BusinessLogic().PropertyAddress.City = addr.City;

            //qc#3206

            _businessLogic.BusinessLogic().PropertyAddress.Save();



            _businessLogic.BusinessLogic().Header = Header.GetHeader(previousSessionId);
            _businessLogic.BusinessLogic().Header.SessionID = qHdr.SessionId;
            _businessLogic.BusinessLogic().Header.RetrieveDateOfBirth = qHdr.RetrieveDateOfBirth;
            _businessLogic.BusinessLogic().Header.RetrieveZipCode = dbZip;

            _businessLogic.BusinessLogic().Header.QuoteNumber = qHdr.QuoteNumber;
            _businessLogic.BusinessLogic().Quote.CompanysQuoteNumber = qHdr.QuoteNumber;
            _businessLogic.BusinessLogic().Quote.Save();

            _businessLogic.BusinessLogic().Header.Save();

            MPQWorkflow mpqWorkflow = MPQWorkflow.GetMPQWorkflow(previousSessionId);
            mpqWorkflow.SessionId = mpqSession.SessionId;
            mpqWorkflow.CalledClaims = _businessLogic.BusinessLogic().Quote.APLUSStatus != null;
            mpqWorkflow.Save();


            try
            {
                _businessLogic.HorizonRetrieve();
                SaveAgencyandMarketingToSession(newQuoteRequest);
            }
            catch (MPQException mpqException)
            {
                throw new FaultException<RetrieveFail>(
                    new RetrieveFail("BuisinessLogic.HorizonRetrieve", mpqException.Message,
                                     qHdr.SessionId),
                    new FaultReason(mpqException.Message)
                    );
            }

            if (!_businessLogic.IsRetrieveDnq())
            {
                //ITR#8945 - Home Business - Check for Contact Customer Care workflow and redirect accordingly.
                if (!_businessLogic.IsRetrieveContactCustomerCare())
                {

                    if (String.IsNullOrEmpty(_businessLogic.BusinessLogic().Quote.AutoPolicyNumber))
                    {
                        QuoteServices.ReapplyDiscount(qHdr, _businessLogic.BusinessLogic().Quote);
                        _businessLogic.ReRate();
                    }
                    _businessLogic.GetUpdatedRetrieveData();

                    _businessLogic.MPQRetrieve(mpqSession, previousSessionId);

                    mpqSession.EcommerceServiceLogId = newQuoteRequest.ECommerceServiceLogId;
                    mpqSession.Save();

                    if (_businessLogic.QuoteExpired())
                    {
                        _businessLogic.BusinessLogic().PrimaryPerson.AutoClaimsRequestID = null; //ITR#9651 Good Driver
                        _businessLogic.BusinessLogic().PrimaryPerson.Save();
                        _businessLogic.MpqSession.EntranceStep = (int)MPQ_PAGES.QuotePage1;
                        _businessLogic.MpqSession.Save();

                        newQuoteReponse.ResponseCode = MPQMessageActionState.ExpiredQuote;
                    }
                    else
                    {
                        _businessLogic.SetRetrieveEntranceStep();

                        newQuoteReponse.ResponseCode = _businessLogic.BusinessLogic().Quote.CompleteQuoteFlag == 1
                                                           ? MPQMessageActionState.CompleteQuote
                                                           : MPQMessageActionState.IncompleteQuote;
                    }


                    newQuoteReponse.SessionId = qHdr.SessionId;
                    newQuoteReponse.EntranceKey = _businessLogic.MpqSession.EntranceKey;
                    newQuoteReponse.RedirectURL = SetRedirectURL(_businessLogic);
                }
                else
                {
                    //ITR#8945 - HomeBusinessEndorement
                    //Contingent Solution for MPQ: Based on the setting in MPQ Partner XML message is either considered as DNQ or Contact Customer Care
                    UIServices objUIServices = new UIServices();
                    newQuoteReponse.ResponseCode = objUIServices.UseDnqResponseStatusForCustomerCare() ? MPQMessageActionState.DNQ : MPQMessageActionState.ContactCustomerCare;
                    newQuoteReponse.ResponseMessage = objUIServices.GetHomeBusinessCustomerCareMessage(_businessLogic.QHeader);

                    mpqSession.EntranceStep = (int)MPQ_PAGES.CombinedRatesPage;
                    mpqSession.Save();
                    newQuoteReponse.EntranceKey = _businessLogic.MpqSession.EntranceKey;
                    newQuoteReponse.RedirectURL = SetRedirectURL(_businessLogic);
                }
            }
            else
            {
                newQuoteReponse.ResponseCode = MPQMessageActionState.DNQ; //"DNQ";

                _businessLogic.RunAllDnqsOnRetrieve();

                string dnqReason = DirectWebDAC.getExternalMPQDNQReason(_businessLogic.BusinessLogic().Quote.DNQReasons, _businessLogic.BusinessLogic().Header.State);

                 //ITR-7425 
				dnqReason = AppendPartnerSpecificInfoToExistingDNQMessage(dnqReason);

                mpqSession.EntranceStep = (int) MPQ_PAGES.CombinedRatesPage;
                mpqSession.Save();

                newQuoteReponse.ResponseMessage = dnqReason;
                newQuoteReponse.EntranceKey = _businessLogic.MpqSession.EntranceKey;
                // MPQRWD
                newQuoteReponse.RedirectURL = SetRedirectURL(_businessLogic);
            }
        }

        private void SaveAgencyandMarketingToSession(MPQDataFillSvcRequest newQuoteRequest)
        {
            // Horizon strips out agency number and marketing id, get them back from MPQ Session.

            if (!String.IsNullOrEmpty(newQuoteRequest.AgencyNumber) || (newQuoteRequest.MarketingId != null))
            {
                bool mustSave = false;
                _businessLogic.BusinessLogic().Quote = Quote.GetQuote(_businessLogic.QHeader.SessionId);


                if (!String.IsNullOrEmpty(newQuoteRequest.AgencyNumber))
                {
                    string agencyNum = newQuoteRequest.AgencyNumber.Substring(0, newQuoteRequest.AgencyNumber.Length > 10 ? 10 : newQuoteRequest.AgencyNumber.Length);

                    _businessLogic.BusinessLogic().Quote.PriorityCode = agencyNum;
                    _businessLogic.MpqSession.AgencyNumber = agencyNum;
                    mustSave = true;
                }

                if (newQuoteRequest.MarketingId != null)
                {
                    _businessLogic.MpqSession.MarketingId = newQuoteRequest.MarketingId.Length > 30 ? newQuoteRequest.MarketingId.Substring(0, 30) : newQuoteRequest.MarketingId;
                    _businessLogic.BusinessLogic().Quote.MarketingId = _businessLogic.MpqSession.MarketingId;
                    mustSave = true;
                }


                if (mustSave)
                {
                    _businessLogic.MpqSession.Save();
                    _businessLogic.BusinessLogic().Quote.Save();
                }
            }
        }

        private QuoteHeader SetQuoteHeaderForRetrieve(Address addr, string quoteNumber, string dbZip,
                                                      string dbDateOfBirth, int dbForm, int dbQuoteCompleteFlag)
        {
            var qHdr = new QuoteHeader
                           {
                               Addr1 = addr.Addr1,
                               Addr2 = addr.Addr2,
                               City = addr.City,
                               CompleteQuoteFlag = dbQuoteCompleteFlag,
                               FormCode = dbForm,
                               IsFirstTime = false,
                               IsRetrieve = true,
                               PartnerId = _mpqPid,
                               QuoteNumber = quoteNumber,
                               RetrieveDateOfBirth = dbDateOfBirth,
                               SessionExists = false,
                               SessionId = DirectWebDAC.GetNewSessionId(_mpqPid, dbForm),
                               State = addr.StateProvCd,
                               Zip = dbZip
                           };
            return qHdr;
        }

        public MPQDataFillSvcResponse NewQuote(MPQDataFillSvcRequest newQuoteRequest)
        {
            newQuoteRequest.RedirectTo = newQuoteRequest.RedirectTo;

            var quoteHeader = new QuoteHeader {PartnerId = _mpqPid, FormCode = newQuoteRequest.Product};

            var qData = new QuoteData();
            if (QuoteServices.UseMailingAddress(newQuoteRequest.MailingAddress1))
            {
                qData.propertyAddressLine1 = newQuoteRequest.MailingAddress1;
                qData.propertyAddressLine2 = newQuoteRequest.MailingAddress2;
                qData.propertyCity = newQuoteRequest.MailingCity;
                qData.propertyState = newQuoteRequest.MailingState;
                qData.propertyZip = newQuoteRequest.MailingZip;
            }
            else
            {
                qData.propertyZip = newQuoteRequest.GaragingZip;
            }

            quoteHeader.Zip = qData.propertyZip;
            quoteHeader.FormCode = newQuoteRequest.Product;
            quoteHeader.MarketingId = newQuoteRequest.MarketingId;

            // need to get session id before we get an instance of the BL for caching
            quoteHeader.SessionId = DirectWebDAC.GetNewSessionId(quoteHeader.PartnerId, quoteHeader.FormCode);
            SavePeopleFromRequestToSession(newQuoteRequest, quoteHeader);


            // update existing MPQ_Session_Master row if it exists
            var newQuoteResponse = new MPQDataFillSvcResponse();
            if (DirectWebDAC.IsMPQQuote(newQuoteRequest.MPQQuoteNumber))
            {
                int mpqId = DirectWebDAC.GetMpqIdFromMpqNumber(newQuoteRequest.MPQQuoteNumber);
                MPQSession session = MPQSession.GetMPQSession(mpqId);
                session.SessionId = quoteHeader.SessionId;
                session.EcommerceServiceLogId = newQuoteRequest.ECommerceServiceLogId;
                session.Save();
            }

            _businessLogic = (MPQBusinessLogic) BusinessLogic.Factory.BusinessLogic.Get(quoteHeader, qData);

            var host = GetDefaultHostFromClient();

            var serviceHost = new ServiceHost
                                  {
                                      SessionId = quoteHeader.SessionId,
                                      Host = host
                                  };
            serviceHost.Save();
            // This is the new changes for MPQRWD.
            int experience = FlashExperience;
            if(newQuoteRequest.Experience.Equals("FQ"))
            {
                experience = FQExperience;
            }
            else if (newQuoteRequest.Experience.Equals("DAQ"))
            {
                experience = DAQExperience;
            }
            _businessLogic.MpqSession.ServiceHost = host;
            _businessLogic.MpqSession.MPQNumber = newQuoteRequest.MPQQuoteNumber;
            _businessLogic.MpqSession.SessionId = quoteHeader.SessionId;
            _businessLogic.MpqSession.EcommerceServiceLogId = newQuoteRequest.ECommerceServiceLogId;
            _businessLogic.MpqSession.Experience = experience;
            _businessLogic.MpqSession.IsExpired = false;
            _businessLogic.MpqSession.ExpirationTime = DateTime.Now.AddMinutes(GetUrlTimeout());
            _businessLogic.MpqSession.EntranceKey = Guid.NewGuid();
            _businessLogic.MpqSession.Save();

            SaveAgencyandMarketingToSession(newQuoteRequest);

            if (String.IsNullOrEmpty(newQuoteRequest.AutoPolicyNumber))
            {
                _businessLogic.BusinessLogic().Quote.CompanysQuoteNumber = String.Empty;
                //_businessLogic.BusinessLogic().Quote.PartnerAutoPolicyQuestion = 0;

                /*PartnerAutoPolicy mapped to PartnerAutoPolicyQuestion in DB*/
                _businessLogic.BusinessLogic().Quote.PartnerAutoPolicy = 0;
                _businessLogic.BusinessLogic().Quote.AutoPolicyNumber = String.Empty;
            }
            else
            {
                _businessLogic.BusinessLogic().Quote.CompanysQuoteNumber = String.Empty;
                //_businessLogic.BusinessLogic().Quote.PartnerAutoPolicyQuestion = 1;

                /*PartnerAutoPolicy mapped to PartnerAutoPolicyQuestion in DB*/
                _businessLogic.BusinessLogic().Quote.PartnerAutoPolicy = 1;
                _businessLogic.BusinessLogic().Quote.AutoPolicyNumber = newQuoteRequest.AutoPolicyNumber;
            }

            _businessLogic.BusinessLogic().Quote.Save();

            try
            {
                _businessLogic.StartNewQuote(newQuoteRequest);

                var responseMessage = _businessLogic.ResponseMessage;
                
                // This was added because, when the user DNQ's this early, the internal message is never translated
                // into an external message.
                if (_businessLogic.IsDnq)
                {
                    if (!string.IsNullOrEmpty(responseMessage) && !responseMessage.Contains("PGR_MESSAGE_TOKEN"))
                    {
                        responseMessage = string.Format("{0}{1}", responseMessage, "PGR_MESSAGE_TOKEN");
                    }

                    responseMessage = AppendPartnerSpecificInfoToExistingDNQMessage(responseMessage);
                }

                newQuoteResponse.SessionId = _businessLogic.QHeader.SessionId;
                newQuoteResponse.ResponseCode = _businessLogic.ResponseCode;
                newQuoteResponse.ResponseMessage = responseMessage;
                newQuoteResponse.EntranceKey = _businessLogic.MpqSession.EntranceKey;
                newQuoteResponse.RedirectURL = SetRedirectURL(_businessLogic);
                
                
            } 
            catch (BusinessLogicException businessLogicException)
            {
                ThreadLog.LogException(businessLogicException);
            }
            catch (Exception ex)
            {
                ThreadLog.LogException(ex);
            }

            return newQuoteResponse;
        }


        private string SetRedirectURL(MPQBusinessLogic bl)
        {
            var config = IQuoteConfiguration.GetConfigurationInstance();
            bool useMobile = config.RedirectToMobileVersion(_businessLogic.MpqSession.Experience, _businessLogic.QHeader.FormCode);
            string redirectURL = string.Empty;

            if (_businessLogic.MpqSession.Experience == DAQExperience && useMobile)
            {
                redirectURL =
                    String.Format(
                        CacheSet<ConfigurationCache, string, StaticSingletonAllocator<ConfigurationCache>>.
                            StaticCollection.Get().MobileRedirectUrl,
                        _businessLogic.MpqSession.EntranceKey);
            }
            else if (_businessLogic.MpqSession.Experience == FQExperience && useMobile)
            {
                redirectURL =
                    String.Format(
                        CacheSet<ConfigurationCache, string, StaticSingletonAllocator<ConfigurationCache>>.
                            StaticCollection.Get().DesktopRedirectUrl,
                        _businessLogic.MpqSession.EntranceKey);
            }
            else
            {
                redirectURL =
                    String.Format(
                        CacheSet<ConfigurationCache, string, StaticSingletonAllocator<ConfigurationCache>>.
                            StaticCollection.Get().RedirectUrl,
                        _businessLogic.MpqSession.EntranceKey);

                if (_businessLogic.MpqSession.Experience != FlashExperience)
                {
                    _businessLogic.MpqSession.Experience = FlashExperience;
                    _businessLogic.MpqSession.Save();
                }
            }

            return redirectURL;
        }


        private static void SavePeopleFromRequestToSession(MPQDataFillSvcRequest newQuoteRequest, QuoteHeader qHdr)
        {
            var primaryEmail = String.Empty;
            if (newQuoteRequest.NamedInsured != null)
            {
                foreach (var namedInsured in newQuoteRequest.NamedInsured)
                {
                    var person = new MPQPerson
                                           {
                                               SessionId = qHdr.SessionId,
                                               FirstName = namedInsured.FirstName,
                                               LastName = namedInsured.LastName,
                                               DateOfBirth = namedInsured.DOB.ToString("yyyyMMdd")
                                           };

                    if (!String.IsNullOrEmpty(namedInsured.SSN))
                    {
                        person.SocialSecurityNumber = (String.IsNullOrEmpty(namedInsured.SSN.Replace(" ", ""))
                                                           ? "000000000"
                                                           : namedInsured.SSN);
                    }
                    else
                    {
                        person.SocialSecurityNumber = "000000000";
                    }

                    person.PrimaryAuto = namedInsured.Type == NamedInsuredType.Primary;
                    person.PgrPersonId = namedInsured.ID;

                    if (person.PrimaryAuto)
                    {
                        primaryEmail = namedInsured.Email;
                    }

                    person.Save();
                }
            }

            foreach (var mpqPerson in MPQPeople.GetMPQPeople(qHdr.SessionId))
            {
                mpqPerson.Email = primaryEmail;
                mpqPerson.Save();
            }
        }

        //public static void SessionPersistence(IQuoteHeader qHeader)
        //{
        //    // the machine which instantiated the business logic should be saved as the service host.
        //    var integrationServices = new IntegrationServices();

        //    var headerIndex = OperationContext.Current.IncomingMessageHeaders.FindHeader(
        //        "_url", 
        //        OperationContext.Current.EndpointDispatcher.ContractNamespace);
        //    if (headerIndex > -1)
        //    {
        //        var requestUrl = OperationContext.Current.IncomingMessageHeaders.GetHeader<string>(headerIndex);


        //        var host = (new Uri(requestUrl)).Host;

        //        var serviceHost = new ServiceHost()
        //                              {
        //                                  Host = host,
        //                                  SessionId = qHeader.SessionId
        //                              };
        //        serviceHost.Save();
        //    } else
        //    {
        //        throw new BusinessLogicException("Couldn't real _url header, so couldn't set service host.");
        //    }
        //}

        public void FlagMPQQuoteAsError(int sessionID)
        {
            MPQSession mpqSession = MPQSession.GetMPQSession(DirectWebDAC.GetMpqIdFromSessionId(sessionID));
            mpqSession.Error = true;
            mpqSession.Save();
        }

        public int GetMPQNumber(int sessionID)
        {
            return DirectWebDAC.GetMpqIdFromSessionId(sessionID);
        }

        public int? GetSessionIdFromMpqNumber(int mpqNumber)
        {
            return DirectWebDAC.GetSessionIdFromMpqNumber(mpqNumber);
        }

        public string GetDefaultConsumerServiceEndPoint(int sessionID)
        {
            MPQSession mpqSession = MPQSession.GetMPQSession(DirectWebDAC.GetMpqIdFromSessionId(sessionID));
            return mpqSession.ServiceHost;
        }

        public string GetDefaultReferrer()
        {
            return IQuoteConfiguration.GetConfigurationInstance().DefaultReferrer;
        }
        public string GetHostForSession(int sessionId)
        {
            var serviceHost = ServiceHost.GetServiceHost(sessionId);
            return serviceHost.Host;
        }

        public string GetDefaultHostFromClient()
        {
            var messageHeaderUri = new Uri(GetRawUriFromMessageHeader());
            return HttpUtility.ParseQueryString(messageHeaderUri.Query).Get("epref");
        }

        /// <summary>
        /// Gets the timeout value for the url in minutes.
        /// </summary>
        /// <returns>time in minutes that the url should be kicked out.</returns>
        public int GetUrlTimeout() 
        {
            int timeout = 0;
            try
            {
                IQuoteConfiguration configuration = IQuoteConfiguration.GetConfigurationInstance();

                string timeoutValue = configuration.Setting("UrlTimeout");
                if (!string.IsNullOrEmpty(timeoutValue))
                {
                    int.TryParse(timeoutValue, out timeout);
                }
               
            }
            catch(Exception ex)
            {
                ECommerceMPQServiceLogger.GetInstance.LogException(ex);
            }

            return timeout;
        }



    }
}
