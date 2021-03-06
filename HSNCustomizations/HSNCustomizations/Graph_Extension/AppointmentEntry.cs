using PX.SM;
using PX.Common;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.AR;
using PX.Objects.IN;
using PX.Objects.CS;
using PX.Objects.CR;
using PX.Objects.CR.Standalone;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using HSNCustomizations.DAC;
using HSNCustomizations.Descriptor;
using PX.Objects.AP;

namespace PX.Objects.FS
{
    public class AppointmentEntry_Extension : PXGraphExtension<AppointmentEntry>
    {
        #region Constant String & Classes
        public const string TransferScr = "IN304000";
        public const string ReceiptScr = "IN301000";
        public const string RMAReqAttr = "RMAREQ";
        public const string BrandAttr = "BRAND";

        public class rMAReqAttrID : PX.Data.BQL.BqlString.Constant<rMAReqAttrID>
        {
            public rMAReqAttrID() : base(RMAReqAttr) { }
        }
        #endregion

        #region Selects
        public SelectFrom<LUMAppEventHistory>.Where<LUMAppEventHistory.srvOrdType.IsEqual<FSAppointment.srvOrdType.FromCurrent>
                                                    .And<LUMAppEventHistory.apptRefNbr.IsEqual<FSAppointment.refNbr.FromCurrent>>>.View EventHistory;

        public SelectFrom<INRegister>.Where<INRegister.docType.IsIn<INDocType.transfer, INDocType.receipt>
                                            .And<INRegisterExt.usrSrvOrdType.IsEqual<FSAppointment.srvOrdType.FromCurrent>
                                                 .And<INRegisterExt.usrAppointmentNbr.IsEqual<FSAppointment.refNbr.FromCurrent>>>>.View INRegisterView;

        public SelectFrom<LUMHSNSetup>.View HSNSetupView;
        #endregion

        #region Override Methods
        public override void Initialize()
        {
            base.Initialize();

            Base.menuDetailActions.AddMenuAction(openPartRequest);
            Base.menuDetailActions.AddMenuAction(openPartReceive);
            Base.menuDetailActions.AddMenuAction(openInitiateRMA);
            Base.menuDetailActions.AddMenuAction(openReturnRMA);
            Base.menuDetailActions.AddMenuAction(toggleRMA);

            FSWorkflowStageHandler.InitStageList();
            AddAllStageButton();
        }
        #endregion

        #region Delegate DataView

        protected virtual IEnumerable staffRecords()
        {
            var staffReuslt = StaffSelectionHelper.StaffRecordsDelegate(Base.AppointmentServiceEmployees,
                                                             Base.SkillGridFilter,
                                                             Base.LicenseTypeGridFilter,
                                                             Base.StaffSelectorFilter);
            // 是否篩選Staff
            var isFilter = Base.AppointmentRecords.Current == null ? false : (FSSrvOrdType.PK.Find(Base, Base.AppointmentRecords.Current.SrvOrdType)?.GetExtension<FSSrvOrdTypeExt>()?.UsrStaffFilterByBranch ?? false);
            // Appointment Record
            var apptCurrent = Base.AppointmentRecords.Current;
            // Appointment Branch LocationID
            var branchLocationID = FSServiceOrder.PK.Find(Base, apptCurrent?.SrvOrdType, apptCurrent?.SORefNbr)?.BranchLocationID;
            // BranchLocationID 實際的BranchID
            var currentBranchID = FSBranchLocation.PK.Find(Base, branchLocationID)?.BranchID;
            foreach (BAccountStaffMember staffItem in staffReuslt)
            {
                // 需要篩選Staff 或 Appointment Current Record != null
                if (isFilter && apptCurrent != null)
                {
                    // Employee Info
                    var employeeInfo = EPEmployee.PK.Find(Base, staffItem.BAccountID);
                    if (employeeInfo != null)
                    {
                        // Find Employee Branch
                        var staffBranchID = SelectFrom<PX.Objects.GL.Branch>
                                       .Where<PX.Objects.GL.Branch.bAccountID.IsEqual<P.AsInt>>
                                       .View.SelectSingleBound(Base, null, employeeInfo.ParentBAccountID)
                                       .TopFirst?.BranchID;
                        if (staffBranchID == currentBranchID)
                            yield return staffItem;
                    }
                }
                else
                    yield return staffItem;
            }
        }
        #endregion

        #region Delegate Methods
        public delegate void PersistDelegate();
        [PXOverride]
        public void Persist(PersistDelegate baseMethod)
        {
            if (Base.AppointmentRecords.Current != null &&
               (SelectFrom<FSSrvOrdType>.Where<FSSrvOrdType.srvOrdType.IsEqual<P.AsString>>.View.Select(Base, Base.AppointmentRecords.Current.SrvOrdType).TopFirst?.GetExtension<FSSrvOrdTypeExt>().UsrEnableEquipmentMandatory ?? false))
            {
                VerifyEquipmentIDMandatory();
            }

            if (Base.AppointmentRecords.Current != null &&
                Base.AppointmentRecords.Current.Status != FSAppointment.status.Values.Closed &&
                HSNSetupView.Select().TopFirst?.EnableHeaderNoteSync == true)
            {
                if (Base.AppointmentRecords.Current.AppointmentID < 0)
                {
                    SyncNoteApptOrSrvOrd(Base, typeof(FSServiceOrder), typeof(FSAppointment));
                }
                else
                {
                    SyncNoteApptOrSrvOrd(Base, typeof(FSAppointment), typeof(FSServiceOrder));
                }
            }

            var isNewData = Base.AppointmentRecords.Cache.Inserted.RowCast<FSAppointment>().Count() > 0;
            // Check Status is Dirty
            var statusDirtyResult = CheckStatusIsDirty(Base.AppointmentRecords.Current);
            // Check Stage is Dirty
            var wfStageDirtyResult = CheckWFStageIsDirty(Base.AppointmentRecords.Current);
            // Detect is New Staff Record & lineType = InventoryItem or Service type
            var newStaffRecords = Base.AppointmentServiceEmployees.Cache.Inserted.RowCast<FSAppointmentEmployee>().ToList();
            // IsNew Detail Record
            FSWorkflowStageHandler.IsNewDetailRecord = Base.AppointmentDetails.Cache.Inserted.RowCast<FSAppointmentDet>().Any(x => x.LineType == "SLPRO" || x.LineType == "SERVI");
            // Set UsrLastSatusModDate if Stage is dirty
            if (wfStageDirtyResult.IsDirty)
                Base.AppointmentRecords.Current.GetExtension<FSAppointmentExt>().UsrLastSatusModDate = PXTimeZoneInfo.Now;

            // 記錄刪除的Details資料[Phase II]
            var detailDeleteRecord = new List<FSAppointmentDet>();
            detailDeleteRecord.AddRange(Base.AppointmentDetails.Cache.Deleted.RowCast<FSAppointmentDet>());

            baseMethod();
            try
            {
                using (PXTransactionScope ts = new PXTransactionScope())
                {
                    // Init object
                    bool isDriveStaff = false;
                    FSWorkflowStageHandler.apptEntry = Base;
                    FSWorkflowStageHandler.InitStageList();

                    // insert log if status is change
                    if (statusDirtyResult.IsDirty && !string.IsNullOrEmpty(statusDirtyResult.oldValue))
                        FSWorkflowStageHandler.InsertEventHistoryForStatus(nameof(AppointmentEntry), statusDirtyResult.oldValue, statusDirtyResult.newValue);

                    LUMAutoWorkflowStage autoWFStage = new LUMAutoWorkflowStage();

                    // check staff is driver
                    foreach (var staff in newStaffRecords)
                    {
                        var employee = EPEmployee.PK.Find(Base, staff.EmployeeID);
                        var attr = CSAnswers.PK.Find(Base, employee?.NoteID, "DRIVER");
                        if (attr != null && attr.Value == "1")
                        {
                            isDriveStaff = true;
                            break;
                        }
                    }

                    #region WorkFlower

                    // New Data
                    if (isNewData)
                        autoWFStage = LUMAutoWorkflowStage.PK.Find(Base, Base.AppointmentRecords.Current.SrvOrdType, nameof(WFRule.OPEN01));
                    // Manual Chagne Stage
                    else if (wfStageDirtyResult.IsDirty && wfStageDirtyResult.oldValue.HasValue && wfStageDirtyResult.newValue.HasValue)
                        autoWFStage = new LUMAutoWorkflowStage()
                        {
                            SrvOrdType = Base.AppointmentRecords.Current.SrvOrdType,
                            WFRule = "MANUAL",
                            Active = true,
                            CurrentStage = wfStageDirtyResult.oldValue,
                            NextStage = wfStageDirtyResult.newValue,
                            Descr = "Manual change Stage"
                        };
                    // Staff Drive stage
                    else if (isDriveStaff)
                        autoWFStage = LUMAutoWorkflowStage.PK.Find(Base, Base.AppointmentRecords.Current.SrvOrdType, nameof(WFRule.ASSIGN03));
                    // Workflow
                    else
                        autoWFStage = FSWorkflowStageHandler.AutoWFStageRule(nameof(AppointmentEntry));

                    if (autoWFStage != null && autoWFStage.Active == true)
                        FSWorkflowStageHandler.UpdateWFStageID(nameof(AppointmentEntry), autoWFStage);

                    #endregion

                    // 執行Base Persisted
                    baseMethod();

                    #region [All-Phase2]Sync Delete Details Record with Service Order Details
                    // 判斷Primary Current是否存在(刪除整張單) 並同步Service Order Details
                    if (Base.AppointmentRecords.Current != null && detailDeleteRecord.Count > 0)
                    {
                        PXTrace.WriteInformation($"Delete Service Order Details (count: {detailDeleteRecord.Count})");
                        var srvGraph = PXGraph.CreateInstance<ServiceOrderEntry>();
                        srvGraph.ServiceOrderRecords.Current = srvGraph.ServiceOrderRecords.Search<FSServiceOrder.refNbr>(Base.AppointmentRecords.Current.SORefNbr, Base.AppointmentRecords.Current.SrvOrdType);
                        if (srvGraph.ServiceOrderRecords.Current != null)
                        {
                            foreach (var deletedItem in detailDeleteRecord)
                            {
                                var currentLine = srvGraph.ServiceOrderDetails.Select().RowCast<FSSODet>().FirstOrDefault(x => x.LineNbr == deletedItem.OrigLineNbr && x.InventoryID == deletedItem.InventoryID);
                                if (currentLine != null)
                                    srvGraph.ServiceOrderDetails.Cache.Delete(currentLine);
                            }
                            srvGraph.Save.Press();
                        }
                    }
                    #endregion

                    ts.Complete();
                }
            }
            catch (PXException)
            {
                throw;
            }
        }

        public delegate IEnumerable CloseAppointmentDelegate(PXAdapter adapter);
        [PXOverride]
        public IEnumerable CloseAppointment(PXAdapter adapter, CloseAppointmentDelegate baseMethod)
        {
            if (Base.AppointmentDetails.Select().RowCast<FSAppointmentDet>().Where(x => x.GetExtension<FSAppointmentDetExt>().UsrRMARequired == true &&
                                                                                        x.Status != FSAppointmentDet.status.CANCELED).Count() > 0)
            {
                if (this.INRegisterView.Select().RowCast<INRegister>().Where(x => x.DocType == INDocType.Receipt &&
                                                                                  x.GetExtension<INRegisterExt>().UsrTransferPurp == LUMTransferPurposeType.RMAInit).Count() <= 0)
                {
                    throw new PXException(HSNMessages.NoInitRMARcpt);
                }

                if (this.INRegisterView.Select().RowCast<INRegister>().Where(x => x.DocType == INDocType.Transfer &&
                                                                                  x.GetExtension<INRegisterExt>().UsrTransferPurp == LUMTransferPurposeType.RMARetu).Count() <= 0)
                {
                    throw new PXException(HSNMessages.MustReturnRMA);
                }
            }

            if (this.INRegisterView.Select().RowCast<INRegister>().Where(x => x.Status != INDocStatus.Released).Count() > 0)
            {
                throw new PXException(HSNMessages.InvtTranNoAllRlsd);
            }

            return baseMethod(adapter);
        }

        public delegate IEnumerable StartAppointmentDelegate(PXAdapter adapter);
        [PXOverride]
        public IEnumerable StartAppointment(PXAdapter adapter, StartAppointmentDelegate baseMethod)
        {
            if (Base.AppointmentServiceEmployees.Select().Count <= 0 && Base.ServiceOrderTypeSelected.Current?.GetExtension<FSSrvOrdTypeExt>().UsrOnStaffIsMandStartAppt == true)
            {
                throw new PXException(HSNMessages.StartApptNoStaff);
            }

            return baseMethod(adapter);
        }

        [PXButton]
        [PXUIField(DisplayName = "Run Appointment Billing", MapEnableRights = PXCacheRights.Select, MapViewRights = PXCacheRights.Select)]
        public virtual IEnumerable invoiceAppointment(PXAdapter adapter)
        {
            var doc = Base.AppointmentRecords.Current;
            var prepeymentInfo = Base.ServiceOrderRelated.Current;
            var customerInfo = Customer.PK.Find(Base, doc.CustomerID);
            if (prepeymentInfo?.SOPrepaymentRemaining > 0 && (customerInfo == null || !customerInfo.PrepaymentAcctID.HasValue || !customerInfo.PrepaymentSubID.HasValue))
                throw new PXException("Please maintain the prepayment account for this customer");

            // [All-Phase2] Add a Validation for Qty Available by Warehouse and Location
            var details = Base.AppointmentDetails.Cache.Cached.RowCast<FSAppointmentDet>().Where(x => x.LineType == "SLPRO");
            foreach (var item in details)
            {
                var inventoryInfo = InventoryItem.PK.Find(Base,item?.InventoryID);
                var itemclass = INItemClass.PK.Find(Base, inventoryInfo?.ItemClassID);
                // Line Status != "Cancel" && Stock Item 才做檢核
                if (item?.Status != "CC" && (itemclass?.StkItem ?? false))
                {
                    if (item.LocationID == null)
                        throw new PXException($"LocationID can not be empty (InventoryID: {item.InventoryCD})");
                    var qtyOnHand = INLocationStatus.PK.Find(Base, item.InventoryID, item.SubItemID, item.SiteID, item.LocationID)?.QtyOnHand ?? 0;
                    if (item.ActualQty > qtyOnHand)
                        throw new PXException($"Inventory quantity for {item.InventoryCD} in warehouse will go negative.");
                }
            }
            return Base.InvoiceAppointment(adapter);
        }
        #endregion

        #region Override DAC
        [PXDBInt]
        [PXDefault(PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIField(DisplayName = "Contact")]
        [PXSelector(typeof(
                   SelectFrom<Contact>
                   .InnerJoin<BAccount>.On<Contact.bAccountID.IsEqual<BAccount.bAccountID>>
                   .Where<Contact.contactType.IsNotEqual<ContactTypesAttribute.bAccountProperty>
                     .And<BAccount.type.IsEqual<BAccountType.customerType>.Or<BAccount.type.IsEqual<BAccountType.prospectType>.Or<BAccount.type.IsEqual<BAccountType.combinedType>>>>
                     .And<BAccount.bAccountID.IsEqual<FSServiceOrder.customerID.FromCurrent>.Or<FSServiceOrder.customerID.FromCurrent.IsEqual<Null>>>>
                   .SearchFor<Contact.contactID>),
           typeof(Contact.contactID),
           typeof(Contact.displayName),
           typeof(Contact.fullName),
           typeof(Contact.title),
           typeof(Contact.eMail),
           typeof(Contact.phone1),
           typeof(Contact.contactType),
           DescriptionField = typeof(Contact.displayName))]
        [PXMergeAttributes(Method = MergeMethod.Replace)]
        public virtual void _(Events.CacheAttached<FSServiceOrder.contactID> e) { }


        [PXDBInt]
        [PXDefault]
        [LUMGetStaffByBranch]
        [PXUIField(DisplayName = "Staff Member", TabOrder = 0)]
        [PXMergeAttributes(Method = MergeMethod.Replace)]
        public virtual void _(Events.CacheAttached<FSAppointmentEmployee.employeeID> e) { }

        [PXDBInt]
        [LUMGetStaffByBranch]
        [PXUIField(DisplayName = "Staff Member ID")]
        [PXMergeAttributes(Method = MergeMethod.Replace)]
        public virtual void _(Events.CacheAttached<FSAppointmentDet.staffID> e) { }

        #endregion

        #region Event Handlers
        protected void _(Events.RowSelected<FSAppointment> e, PXRowSelected baseHandler)
        {
            baseHandler?.Invoke(e.Cache, e.Args);

            EventHistory.AllowDelete = EventHistory.AllowInsert = EventHistory.AllowUpdate = INRegisterView.AllowDelete = INRegisterView.AllowInsert = INRegisterView.AllowUpdate = false;

            LUMHSNSetup hSNSetup = HSNSetupView.Select();

            bool activePartRequest = hSNSetup?.EnablePartReqInAppt == true;
            bool activeRMAProcess = hSNSetup?.EnableRMAProcInAppt == true;
            bool activeWFStageCtrl = hSNSetup?.EnableWFStageCtrlInAppt == true;

            openPartRequest.SetEnabled(activePartRequest);
            openPartReceive.SetEnabled(activePartRequest);
            openInitiateRMA.SetEnabled(activeRMAProcess);
            openReturnRMA.SetEnabled(activeRMAProcess);
            toggleRMA.SetEnabled(activeRMAProcess);

            Base.menuDetailActions.SetVisible(nameof(OpenPartRequest), activePartRequest);
            Base.menuDetailActions.SetVisible(nameof(OpenPartReceive), activePartRequest);
            Base.menuDetailActions.SetVisible(nameof(OpenInitiateRMA), activeRMAProcess);
            Base.menuDetailActions.SetVisible(nameof(OpenReturnRMA), activeRMAProcess);
            Base.menuDetailActions.SetVisible(nameof(ToggleRMA), activeRMAProcess);

            openPartRequest.SetDisplayOnMainToolbar(false);
            openPartReceive.SetDisplayOnMainToolbar(false);
            openInitiateRMA.SetDisplayOnMainToolbar(false);
            openReturnRMA.SetDisplayOnMainToolbar(false);
            toggleRMA.SetDisplayOnMainToolbar(false);

            lumStages.SetVisible(activeWFStageCtrl);

            EventHistory.AllowSelect = activeWFStageCtrl;
            INRegisterView.AllowSelect = activePartRequest;

            PXUIFieldAttribute.SetVisible<FSAppointmentExt.usrTransferToHQ>(e.Cache, e.Row, hSNSetup?.DisplayTransferToHQ ?? false);
            PXUIFieldAttribute.SetVisible<FSAppointmentDetExt.usrRMARequired>(Base.AppointmentDetails.Cache, null, activeRMAProcess);
            PXUIFieldAttribute.SetVisible<FSAppointmentDetExt.usrIsDOA>(Base.AppointmentDetails.Cache, null, activePartRequest);

            SettingStageButton();
        }

        protected void _(Events.FieldUpdated<FSAppointmentExt.usrTransferToHQ> e)
        {
            if (e.NewValue != null && (bool)e.NewValue == true)
            {
                FSWorkflowStageHandler.apptEntry = Base;
                FSWorkflowStageHandler.InsertEventHistory(nameof(AppointmentEntry), new LUMAutoWorkflowStage()
                {
                    SrvOrdType = Base.AppointmentRecords.Current.SrvOrdType,
                    Descr = PXUIFieldAttribute.GetDisplayName(e.Cache, nameof(FSAppointmentExt.UsrTransferToHQ))
                }); ;
            }
        }

        public void _(Events.FieldUpdated<FSAppointment.finished> e, PXFieldUpdated baseHandler)
        {
            baseHandler?.Invoke(e.Cache, e.Args);
            var row = Base.AppointmentSelected.Current;
            if ((this.HSNSetupView.Select().TopFirst?.EnableAppointmentUpdateEndDate ?? false) && row != null)
            {
                if ((bool)e.NewValue && !row.ActualDateTimeEnd.HasValue)
                    Base.AppointmentSelected.SetValueExt<FSAppointment.actualDateTimeEnd>(row, PX.Common.PXTimeZoneInfo.Now);
                else if (!(bool)e.NewValue)
                    Base.AppointmentSelected.SetValueExt<FSAppointment.actualDateTimeEnd>(row, null);
            }
        }

        protected void _(Events.RowUpdated<FSServiceOrder> e, PXRowUpdated baseHandler)
        {
            baseHandler?.Invoke(e.Cache, e.Args);

            if (e.OldRow.ContactID != e.Row.ContactID)
            {
                ServiceOrderEntry_Extension.SetSrvContactInfo(Base.ServiceOrder_Contact.Cache, e.Row.ContactID, e.Row.ServiceOrderContactID);
            }
        }

        #endregion

        #region Actions
        public PXAction<FSAppointment> openPartRequest;
        [PXUIField(DisplayName = HSNMessages.PartRequest, MapEnableRights = PXCacheRights.Select, MapViewRights = PXCacheRights.Select)]
        [PXButton]
        public virtual void OpenPartRequest()
        {
            INTransferEntry transferEntry = PXGraph.CreateInstance<INTransferEntry>();

            InitTransferEntry(ref transferEntry, Base, HSNMessages.PartRequest);

            OpenNewForm(transferEntry, TransferScr);
        }

        public PXAction<FSAppointment> openPartReceive;
        [PXUIField(DisplayName = HSNMessages.PartReceive, MapEnableRights = PXCacheRights.Select, MapViewRights = PXCacheRights.Select)]
        [PXButton]
        public virtual void OpenPartReceive()
        {
            if (this.INRegisterView.Select().RowCast<INRegister>().Where(x => x.DocType == INDocType.Transfer && x.Released == true &&
                                                                              x.GetExtension<INRegisterExt>().UsrTransferPurp == LUMTransferPurposeType.PartReq).Count() <= 0)
            {
                throw new PXException(HSNMessages.PartReqNotRlsd);
            }

            string transferNbr = null;

            foreach (INRegister row in INRegisterView.Select().RowCast<INRegister>().Where(x => x.DocType == INDocType.Transfer && x.Released == true && x.TransferType == INTransferType.TwoStep))
            {
                transferNbr = row.RefNbr;
            }

            INReceiptEntry receiptEntry = PXGraph.CreateInstance<INReceiptEntry>();

            InitReceiptEntry(ref receiptEntry, Base, transferNbr);

            //BlankReceipt:
            OpenNewForm(receiptEntry, ReceiptScr);
        }

        public PXAction<FSAppointment> openInitiateRMA;
        [PXUIField(DisplayName = HSNMessages.InitiateRMA, MapEnableRights = PXCacheRights.Select, MapViewRights = PXCacheRights.Select)]
        [PXButton]
        public virtual void OpenInitiateRMA()
        {
            var details = Base.AppointmentDetails.Select().RowCast<FSAppointmentDet>().ToList();

            details.RemoveAll(r => r.GetExtension<FSAppointmentDetExt>()?.UsrRMARequired != true || r.Status == FSAppointmentDet.status.CANCELED);

            if (details.Count <= 0)
            {
                throw new PXException(HSNMessages.NoRMARequired);
            }

            INReceiptEntry receiptEntry = PXGraph.CreateInstance<INReceiptEntry>();

            InitReceiptEntry(ref receiptEntry, Base);

            OpenNewForm(receiptEntry, ReceiptScr);
        }

        public PXAction<FSAppointment> openReturnRMA;
        [PXUIField(DisplayName = HSNMessages.ReturnRMA, MapEnableRights = PXCacheRights.Select, MapViewRights = PXCacheRights.Select)]
        [PXButton]
        public virtual void OpenReturnRMA()
        {
            if (new PXView(Base, true, this.INRegisterView.View.BqlSelect).SelectMulti().RowCast<INRegister>().Where(x => x.DocType == INDocType.Receipt &&
                                                                                                                          x.Status == INDocStatus.Released &&
                                                                                                                          x.GetExtension<INRegisterExt>().UsrTransferPurp == LUMTransferPurposeType.RMAInit).Count() <= 0)
            {
                throw new PXException(HSNMessages.ReturnRMAB4Init);
            }

            INTransferEntry transferEntry = PXGraph.CreateInstance<INTransferEntry>();

            InitTransferEntry(ref transferEntry, Base, HSNMessages.RMAReturned);

            OpenNewForm(transferEntry, TransferScr);
        }

        public PXMenuAction<FSAppointment> lumStages;
        [PXUIField(DisplayName = "STAGES", MapEnableRights = PXCacheRights.Select)]
        [PXButton(MenuAutoOpen = true, CommitChanges = true)]
        public virtual void LumStages() { }

        public PXAction<FSAppointment> toggleRMA;
        [PXUIField(DisplayName = HSNMessages.ToggleRMA, MapEnableRights = PXCacheRights.Select, MapViewRights = PXCacheRights.Select)]
        [PXButton]
        public virtual void ToggleRMA()
        {
            var apptDetails = Base.AppointmentDetails.Current;

            if (apptDetails.LineType != ID.LineType_ALL.INVENTORY_ITEM)
            {
                throw new PXSetPropertyException<FSAppointmentDetExt.usrRMARequired>(HSNMessages.ApptLineTypeInvt);
            }

            if (INRegisterView.Select().RowCast<INRegister>().Where(w => w.GetExtension<INRegisterExt>().UsrTransferPurp == LUMTransferPurposeType.RMAInit ||
                                                                         w.GetExtension<INRegisterExt>().UsrTransferPurp == LUMTransferPurposeType.RMARetu).Count() > 0)
            {
                throw new PXException(HSNMessages.CannotToggleRMA);
            }

            bool rMAReq = apptDetails.GetExtension<FSAppointmentDetExt>().UsrRMARequired ?? false;

            if (apptDetails.Status != FSAppointmentDet.status.CANCELED)
            {
                Base.AppointmentDetails.Cache.SetValue<FSAppointmentDetExt.usrRMARequired>(apptDetails, !rMAReq);
                Base.AppointmentDetails.Update(apptDetails);

                FSWorkflowStageHandler.apptEntry = Base;
                FSWorkflowStageHandler.InsertEventHistory(nameof(AppointmentEntry), new LUMAutoWorkflowStage()
                {
                    WFRule = PX.Objects.Common.Messages.Actions,
                    Descr = HSNMessages.ToggleRMA + " [" + apptDetails.InventoryCD + "] To " + (rMAReq == true ? "Unchecked" : "Checked"),
                    CurrentStage = Base.AppointmentRecords.Current?.WFStageID
                });

                Base.Save.Press();
            }
        }
        #endregion

        #region Static Methods
        /// <summary>
        /// Manually insert records into the transfer data view from appointment to open a new window.
        /// </summary>
        /// <param name="transferEntry"></param>
        /// <param name="apptEntry"></param>
        public static void InitTransferEntry(ref INTransferEntry transferEntry, AppointmentEntry apptEntry, string descrType = null)
        {
            FSAppointment appointment = apptEntry.AppointmentSelected.Current;
            INRegister register = transferEntry.CurrentDocument.Cache.CreateInstance() as INRegister;
            INRegisterExt regisExt = register.GetExtension<INRegisterExt>();
            LUMBranchWarehouse branchWH = LUMBranchWarehouse.PK.Find(apptEntry, apptEntry.Accessinfo.BranchID);

            bool isRMA = descrType == HSNMessages.RMAReturned;

            register.TransferType = INTransferType.TwoStep;
            register.ExtRefNbr = $"{appointment.SrvOrdType} | {apptEntry.ServiceOrderRelated.Current?.CustWorkOrderRefNbr} | {apptEntry.ServiceOrderRelated.Current?.CustPORefNbr}";
            register.TranDesc = descrType + " | " + appointment.DocDesc;
            regisExt.UsrSrvOrdType = appointment.SrvOrdType;
            regisExt.UsrAppointmentNbr = appointment.RefNbr;
            regisExt.UsrSORefNbr = appointment.SORefNbr;
            regisExt.UsrTransferPurp = isRMA ? LUMTransferPurposeType.RMARetu : LUMTransferPurposeType.PartReq;

            if (apptEntry.ServiceOrderTypeSelected.Current?.GetExtension<FSSrvOrdTypeExt>().UsrBringBrandAttr2Txfr == true)
            {
                transferEntry.CurrentDocument.Cache.SetValueExt(register, CS.Messages.Attribute + BrandAttr, apptEntry.Answers.Select().RowCast<CSAnswers>().Where(x => x.AttributeID == BrandAttr).FirstOrDefault().Value);
            }

            transferEntry.CurrentDocument.Insert(register);

            PXView view = new PXView(apptEntry, true, apptEntry.AppointmentDetails.View.BqlSelect);

            var list = view.SelectMulti().RowCast<FSAppointmentDet>().Where(x => x.LineType == ID.LineType_ALL.INVENTORY_ITEM);

            if (isRMA == true)
            {
                list = view.SelectMulti().RowCast<FSAppointmentDet>().Where(x => x.LineType == ID.LineType_ALL.INVENTORY_ITEM && x.GetExtension<FSAppointmentDetExt>().UsrRMARequired == true);
            }

            transferEntry.CurrentDocument.Current.SiteID = isRMA ? GetFaultyWFByBranch(transferEntry, transferEntry.Accessinfo.BranchID) : branchWH?.SiteID;
            transferEntry.CurrentDocument.Current.ToSiteID = isRMA ? branchWH?.FaultySiteID : list.FirstOrDefault<FSAppointmentDet>()?.SiteID;
            transferEntry.CurrentDocument.UpdateCurrent();

            foreach (FSAppointmentDet row in list)
            {
                if (row.Status != ID.Status_AppointmentDet.CANCELED &&
                    (row.GetExtension<FSAppointmentDetExt>().UsrIsDOA == true ||
                     isRMA == true ||
                     SelectFrom<INRegister>.InnerJoin<INTran>.On<INTran.docType.IsEqual<INRegister.docType>
                                                                 .And<INTran.refNbr.IsEqual<INRegister.refNbr>>>
                                           .Where<INRegister.docType.IsEqual<INDocType.transfer>
                                                  .And<INRegisterExt.usrSrvOrdType.IsEqual<@P.AsString>
                                                       .And<INRegisterExt.usrAppointmentNbr.IsEqual<@P.AsString>
                                                            .And<INRegisterExt.usrTransferPurp.IsEqual<LUMTransferPurposeType.partReq>
                                                                 .And<INTran.inventoryID.IsEqual<@P.AsInt>>>>>>.View.Select(apptEntry, appointment.SrvOrdType, appointment.RefNbr, row.InventoryID).Count <= 0))
                {
                    CreateINTran(transferEntry, row, false, isRMA == false);
                }
            }

            if (transferEntry.transactions.Cache.Inserted.Count() <= 0) { throw new PXException(HSNMessages.NoPartRequest); }
        }

        /// <summary>
        /// Manually insert records into the receipt data view from appointment to open a new window.
        /// </summary>
        /// <param name="receiptEntry"></param>
        /// <param name="apptEntry"></param>
        public static void InitReceiptEntry(ref INReceiptEntry receiptEntry, AppointmentEntry apptEntry, string transferNbr = null)
        {
            FSAppointment appointment = apptEntry.AppointmentSelected.Current;

            INRegister register = receiptEntry.CurrentDocument.Cache.CreateInstance() as INRegister;
            INRegisterExt regisExt = register.GetExtension<INRegisterExt>();

            register.ExtRefNbr = $"{appointment.SrvOrdType} | {apptEntry.ServiceOrderRelated.Current?.CustWorkOrderRefNbr} | {apptEntry.ServiceOrderRelated.Current?.CustPORefNbr}";
            register.TranDesc = $"{(!string.IsNullOrEmpty(transferNbr) ? HSNMessages.PartReceive : HSNMessages.RMAInitiated)} | {appointment.DocDesc}";
            regisExt.UsrSrvOrdType = appointment.SrvOrdType;
            regisExt.UsrAppointmentNbr = appointment.RefNbr;
            regisExt.UsrSORefNbr = appointment.SORefNbr;
            regisExt.UsrTransferPurp = !string.IsNullOrEmpty(transferNbr) ? LUMTransferPurposeType.PartRcv : LUMTransferPurposeType.RMAInit;

            if (apptEntry.ServiceOrderTypeSelected.Current?.GetExtension<FSSrvOrdTypeExt>().UsrBringBrandAttr2Txfr == true)
            {
                receiptEntry.CurrentDocument.Cache.SetValueExt(register, CS.Messages.Attribute + BrandAttr, apptEntry.Answers.Select().RowCast<CSAnswers>().Where(x => x.AttributeID == BrandAttr).FirstOrDefault().Value);
            }

            register = receiptEntry.CurrentDocument.Insert(register);

            if (string.IsNullOrEmpty(transferNbr))
            {
                PXView view = new PXView(apptEntry, true, apptEntry.AppointmentDetails.View.BqlSelect);

                var list = view.SelectMulti().RowCast<FSAppointmentDet>().Where(x => x.LineType == ID.LineType_ALL.INVENTORY_ITEM && x.Status != FSAppointmentDet.status.CANCELED && x.GetExtension<FSAppointmentDetExt>().UsrRMARequired == true);

                foreach (FSAppointmentDet row in list)
                {
                    CreateINTran(receiptEntry, row, true);
                }
            }
            else
            {
                register.TransferNbr = transferNbr;

                receiptEntry.CurrentDocument.Update(register);
            }
        }

        /// <summary>
        /// Create IN trans record from appointment.
        /// </summary>
        /// <param name="graph"></param>
        /// <param name="apptDet"></param>
        /// <param name="defective"></param>
        public static void CreateINTran(PXGraph graph, FSAppointmentDet apptDet, bool defective = false, bool overrideLocation = false)
        {
            INTran iNTran = new INTran()
            {
                InventoryID = apptDet.InventoryID,
                Qty = apptDet.EstimatedQty
            };

            if (defective == true) { iNTran.SiteID = GetFaultyWFByBranch(graph, apptDet.BranchID); }

            if (overrideLocation == true) { iNTran.ToLocationID = apptDet.SiteLocationID; }

            iNTran = graph.Caches[typeof(INTran)].Insert(iNTran) as INTran;

            iNTran.GetExtension<INTranExt>().UsrApptLineRef = apptDet.LineRef;

            graph.Caches[typeof(INTran)].Update(iNTran);
        }

        /// <summary>
        /// Redirect to the specified form.
        /// </summary>
        /// <param name="graph"></param>
        /// <param name="screenID"></param>
        private static void OpenNewForm(PXGraph graph, string screenID)
        {
            throw new PXRedirectRequiredException(graph, false, PXSiteMap.Provider.FindSiteMapNodeByScreenID(screenID).Title)
            {
                Mode = PXBaseRedirectException.WindowMode.New
            };
        }

        /// <summary>
        /// Enable Header Note Sync between Service Order and Appointment.
        /// </summary>
        /// <param name="graph"></param>
        /// <param name="fromType"></param>
        /// <param name="toType"></param>
        public static void SyncNoteApptOrSrvOrd(PXGraph graph, System.Type fromType, System.Type toType) => PXNoteAttribute.CopyNoteAndFiles(graph.Caches[fromType], graph.Caches[fromType].Current,
                                                                                                                                             graph.Caches[toType], graph.Caches[toType].Current, true, false);

        /// <summary>
        /// Get faulty warehouse by branch which only uses for RMA customization.
        /// </summary>
        /// <param name="graph"></param>
        /// <param name="branchID"></param>
        /// <returns></returns>
        public static int? GetFaultyWFByBranch(PXGraph graph, int? branchID)
        {
            return SelectFrom<INSite>.Where<INSite.branchID.IsEqual<@P.AsInt>
                                            .And<INSiteExt.usrIsFaultySite.IsEqual<True>>>.View.Select(graph, branchID).TopFirst?.SiteID;
        }
        #endregion

        #region Methods
        /// <summary>Check Status Is Drity </summary>
        public (bool IsDirty, string oldValue, string newValue) CheckStatusIsDirty(FSAppointment row)
        {
            if (row == null)
                return (false, string.Empty, string.Empty);

            string oldVale = SelectFrom<FSAppointment>
                               .Where<FSAppointment.srvOrdType.IsEqual<P.AsString>
                                   .And<FSAppointment.refNbr.IsEqual<P.AsString>>>
                                .View.Select(new PXGraph(), row.SrvOrdType, row.RefNbr)
                                .RowCast<FSAppointment>()?.FirstOrDefault()?.Status;
            string newValue = row.Status;

            return (!string.IsNullOrEmpty(oldVale) && oldVale != newValue, oldVale, newValue);
        }

        /// <summary>Check Stage Is Dirty </summary>
        public (bool IsDirty, int? oldValue, int? newValue) CheckWFStageIsDirty(FSAppointment row)
        {
            if (row == null)
                return (false, null, null);

            int? oldVale = SelectFrom<FSAppointment>
                               .Where<FSAppointment.srvOrdType.IsEqual<P.AsString>
                                   .And<FSAppointment.refNbr.IsEqual<P.AsString>>>
                                .View.Select(new PXGraph(), row.SrvOrdType, row.RefNbr)
                                .RowCast<FSAppointment>()?.FirstOrDefault()?.WFStageID;
            int? newValue = row.WFStageID;

            return (oldVale.HasValue && oldVale != newValue, oldVale, newValue);
        }

        /// <summary> Add All Stage Button </summary>
        public void AddAllStageButton()
        {
            var primatryView = Base.AppointmentRecords.Cache.GetItemType();
            var list = FSWorkflowStageHandler.stageList.Select(x => new { x.WFStageID, x.WFStageCD }).Distinct();
            var actionLst = new List<PXAction>();
            foreach (var item in list)
            {
                var temp = PXNamedAction.AddAction(Base, primatryView, item.WFStageCD, item.WFStageCD,
                    adapter =>
                    {
                        var row = Base.AppointmentRecords.Current;
                        if (row != null)
                        {
                            var srvOrderData = FSSrvOrdType.PK.Find(Base, row.SrvOrdType);
                            var stageList = FSWorkflowStageHandler.stageList.Where(x => x.WFID == srvOrderData.SrvOrdTypeID);
                            var currStageIDByType = stageList.Where(x => x.WFStageCD == item.WFStageCD).FirstOrDefault().WFStageID;
                            Base.AppointmentRecords.Cache.SetValueExt<FSAppointment.wFStageID>(Base.AppointmentRecords.Current, currStageIDByType);
                            Base.AppointmentRecords.Cache.MarkUpdated(Base.AppointmentRecords.Current);
                            Base.AppointmentRecords.Update(Base.AppointmentRecords.Current);

                            Base.Persist();
                        }
                        return adapter.Get();
                    },
                    new PXEventSubscriberAttribute[] { new PXButtonAttribute() { CommitChanges = true } }
                );
                actionLst.Add(temp);
            }
            foreach (var act in actionLst)
            {
                act.SetDisplayOnMainToolbar(false);
                this.lumStages.AddMenuAction(act);
            }
        }

        /// <summary> Setting Stage Button Status </summary>
        public void SettingStageButton()
        {
            var isAdmin = SelectFrom<UsersInRoles>
                              .Where<UsersInRoles.rolename.IsEqual<P.AsString>
                                    .And<UsersInRoles.username.IsEqual<P.AsString>>>
                              .View.Select(Base, "Administrator", PXAccess.GetUserName())
                              .Count > 0;
            var row = Base.AppointmentRecords.Current;

            if (row != null && !string.IsNullOrEmpty(row.SrvOrdType))
            {
                List<PXResult<LumStageControl>> lists = SelectFrom<LumStageControl>.Where<LumStageControl.srvOrdType.IsEqual<P.AsString>
                                                                                          .And<LumStageControl.currentStage.IsEqual<P.AsInt>>>
                                                                                   .View.Select(Base, row.SrvOrdType, row.WFStageID).ToList();
                var btn = this.lumStages.GetState(null) as PXButtonState;

                if (btn.Menus != null)
                {
                    foreach (ButtonMenu btnMenu in btn.Menus)
                    {
                        var isVisible = lists.Exists(x => (!(x.GetItem<LumStageControl>().AdminOnly ?? false) || ((x.GetItem<LumStageControl>().AdminOnly ?? false) && isAdmin ? true : false)) && FSWorkflowStageHandler.GetStageName(x.GetItem<LumStageControl>().ToStage) == btnMenu.Command);
                        this.lumStages.SetVisible(btnMenu.Command, isVisible);
                    }
                }
            }
        }

        /// <summary> Check Equipment ID is Mandatory </summary>
        public void VerifyEquipmentIDMandatory()
        {
            var details = Base.AppointmentDetails.Select();
            foreach (FSAppointmentDet item in details)
            {
                if (item.LineType == "SERVI" && !item.SMEquipmentID.HasValue)
                    throw new PXException("Target Equipment ID cannot be blank for service");
            }
        }

        #endregion
    }
}