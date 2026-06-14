# SSIS Project Lineage Documentation
Generated on: 2026-06-14 06:58:00

## Packages Included
- **Stage** (GUID: `{0B3BB49A-C382-47E0-9A08-B4FF93332480}`)
  - Path: `C:\Project\SSIS\SSIS_Documentation\SSIS_Documentation\Stage.dtsx`
- **DW_Load** (GUID: `{6CE5C704-B864-4E9D-A608-1ACE53E51E03}`)
  - Path: `C:\Project\SSIS\SSIS_Documentation\SSIS_Documentation\DW_Load.dtsx`

## Tasks
| Package | Task | Type |
| --- | --- | --- |
| Stage | Customers | `Microsoft.ExecuteSQLTask` |
| Stage | Load DW Customers | `Microsoft.ExecutePackageTask` |
| DW_Load | Load Dim Customers | `Microsoft.Pipeline` |
| Stage | Order Items | `Microsoft.ExecuteSQLTask` |
| Stage | Orders | `Microsoft.ExecuteSQLTask` |
| Stage | Start | `Microsoft.ExecuteSQLTask` |

## Data Flow Components
| Package | Task | Component | Type | Connection | SQL / Table |
| --- | --- | --- | --- | --- | --- |
| Stage | Customers | Customers | `Execute SQL Task` | `{AF2C2600-2DCE-498A-AC64-A7FDC44BDB39}` | `stage.usp_stage_load_customers` |
| DW_Load | Load Dim Customers | OLE DB Destination | `Microsoft.OLEDBDestination` | `Project.ConnectionManagers[DW]` | `[DW].[Dim_Customers]` |
| DW_Load | Load Dim Customers | OLE DB Source | `Microsoft.OLEDBSource` | `Project.ConnectionManagers[Staging]` | `[stage].[usp_Get_LoadCustomers]` |
| Stage | Order Items | Order Items | `Execute SQL Task` | `{AF2C2600-2DCE-498A-AC64-A7FDC44BDB39}` | `stage.usp_stage_load_orderitems` |
| Stage | Orders | Orders | `Execute SQL Task` | `{AF2C2600-2DCE-498A-AC64-A7FDC44BDB39}` | `stage.usp_stage_load_orders` |
| Stage | Start | Start | `Execute SQL Task` | `{AF2C2600-2DCE-498A-AC64-A7FDC44BDB39}` | `SELECT 1` |

## Data Flow Paths
| From Component | To Component | Path |
| --- | --- | --- |
| `Package\Load Dim Customers\OLE DB Source` | `Package\Load Dim Customers\OLE DB Destination` | `Package\Load Dim Customers.Paths[OLE DB Source Output]` |

## Column-level Lineage Mappings
| Package | Task | Source Component | Source Column | Destination Component | Destination Column | Operation |
| --- | --- | --- | --- | --- | --- | --- |
| DW_Load | Load Dim Customers | OLE DB Source | `SourceCustomerID` | DW.Dim_Customers | `SourceCustomerID` | XML_FALLBACK |
| DW_Load | Load Dim Customers | OLE DB Source | `CustomerCode` | DW.Dim_Customers | `CustomerCode` | XML_FALLBACK |
| DW_Load | Load Dim Customers | OLE DB Source | `FirstName` | DW.Dim_Customers | `FirstName` | XML_FALLBACK |
| DW_Load | Load Dim Customers | OLE DB Source | `LastName` | DW.Dim_Customers | `LastName` | XML_FALLBACK |
| DW_Load | Load Dim Customers | OLE DB Source | `Email` | DW.Dim_Customers | `Email` | XML_FALLBACK |
| DW_Load | Load Dim Customers | OLE DB Source | `Phone` | DW.Dim_Customers | `Phone` | XML_FALLBACK |
| Stage | Start | Start | `` | Start | `` | SELECT |
| Stage | Customers | usp_stage_load_customers | `` | stage.Customers_stg | `LoadBatchID` | SQL_PROC_INSERT |
| Stage | Customers | usp_stage_load_customers | `` | stage.Customers_stg | `LoadDate` | SQL_PROC_INSERT |
| Stage | Customers | source.Customers | `CustomerID` | stage.Customers_stg | `SourceCustomerID` | SQL_PROC_INSERT |
| Stage | Customers | source.Customers | `CustomerCode` | stage.Customers_stg | `CustomerCode` | SQL_PROC_INSERT |
| Stage | Customers | source.Customers | `FirstName` | stage.Customers_stg | `FirstName` | SQL_PROC_INSERT |
| Stage | Customers | source.Customers | `LastName` | stage.Customers_stg | `LastName` | SQL_PROC_INSERT |
| Stage | Customers | source.Customers | `Email` | stage.Customers_stg | `Email` | SQL_PROC_INSERT |
| Stage | Customers | source.Customers | `Phone` | stage.Customers_stg | `Phone` | SQL_PROC_INSERT |
| Stage | Customers | usp_stage_load_customers | `` | stage.Customers_stg | `LoadStatus` | SQL_PROC_INSERT |
| Stage | Customers | source.Customers | `CustomerCode` | stage.Customers_stg | `CustomerCode` | SQL_PROC_UPDATE |
| Stage | Customers | source.Customers | `FirstName` | stage.Customers_stg | `FirstName` | SQL_PROC_UPDATE |
| Stage | Customers | source.Customers | `LastName` | stage.Customers_stg | `LastName` | SQL_PROC_UPDATE |
| Stage | Customers | source.Customers | `Email` | stage.Customers_stg | `Email` | SQL_PROC_UPDATE |
| Stage | Customers | source.Customers | `Phone` | stage.Customers_stg | `Phone` | SQL_PROC_UPDATE |
| DW_Load | Load Dim Customers | stage.Customers_stg | `SourceCustomerID` | OLE DB Source | `SourceCustomerID` | SQL_PROC_SELECT |
| DW_Load | Load Dim Customers | stage.Customers_stg | `CustomerCode` | OLE DB Source | `CustomerCode` | SQL_PROC_SELECT |
| DW_Load | Load Dim Customers | stage.Customers_stg | `FirstName` | OLE DB Source | `FirstName` | SQL_PROC_SELECT |
| DW_Load | Load Dim Customers | stage.Customers_stg | `LastName` | OLE DB Source | `LastName` | SQL_PROC_SELECT |
| DW_Load | Load Dim Customers | stage.Customers_stg | `Email` | OLE DB Source | `Email` | SQL_PROC_SELECT |
| DW_Load | Load Dim Customers | stage.Customers_stg | `Phone` | OLE DB Source | `Phone` | SQL_PROC_SELECT |
| Stage | Order Items | usp_stage_load_orderitems | `` | stage.OrderItems_stg | `LoadBatchID` | SQL_PROC_INSERT |
| Stage | Order Items | usp_stage_load_orderitems | `` | stage.OrderItems_stg | `LoadDate` | SQL_PROC_INSERT |
| Stage | Order Items | source.OrderItems | `OrderItemID` | stage.OrderItems_stg | `SourceOrderItemID` | SQL_PROC_INSERT |
| Stage | Order Items | source.Orders | `OrderID` | stage.OrderItems_stg | `SourceOrderID` | SQL_PROC_INSERT |
| Stage | Order Items | source.OrderItems | `OrderID` | stage.OrderItems_stg | `SourceOrderID` | SQL_PROC_INSERT |
| Stage | Order Items | source.OrderItems | `ProductCode` | stage.OrderItems_stg | `ProductCode` | SQL_PROC_INSERT |
| Stage | Order Items | source.OrderItems | `Quantity` | stage.OrderItems_stg | `Quantity` | SQL_PROC_INSERT |
| Stage | Order Items | source.OrderItems | `UnitPrice` | stage.OrderItems_stg | `UnitPrice` | SQL_PROC_INSERT |
| Stage | Order Items | source.OrderItems | `LineTotal` | stage.OrderItems_stg | `LineTotal` | SQL_PROC_INSERT |
| Stage | Order Items | source.Orders | `OrderID` | stage.OrderItems_stg | `LoadStatus` | SQL_PROC_INSERT |
| Stage | Order Items | source.Orders | `OrderID` | stage.OrderItems_stg | `ErrorMessage` | SQL_PROC_INSERT |
| Stage | Order Items | source.Orders | `OrderID` | stage.OrderItems_stg | `SourceOrderID` | SQL_PROC_UPDATE |
| Stage | Order Items | source.OrderItems | `OrderID` | stage.OrderItems_stg | `SourceOrderID` | SQL_PROC_UPDATE |
| Stage | Order Items | source.OrderItems | `ProductCode` | stage.OrderItems_stg | `ProductCode` | SQL_PROC_UPDATE |
| Stage | Order Items | source.OrderItems | `Quantity` | stage.OrderItems_stg | `Quantity` | SQL_PROC_UPDATE |
| Stage | Order Items | source.OrderItems | `UnitPrice` | stage.OrderItems_stg | `UnitPrice` | SQL_PROC_UPDATE |
| Stage | Order Items | source.OrderItems | `LineTotal` | stage.OrderItems_stg | `LineTotal` | SQL_PROC_UPDATE |
| Stage | Order Items | source.Orders | `OrderID` | stage.OrderItems_stg | `LoadStatus` | SQL_PROC_UPDATE |
| Stage | Order Items | source.Orders | `OrderID` | stage.OrderItems_stg | `ErrorMessage` | SQL_PROC_UPDATE |
| Stage | Orders | usp_stage_load_orders | `` | stage.Orders_stg | `LoadBatchID` | SQL_PROC_INSERT |
| Stage | Orders | usp_stage_load_orders | `` | stage.Orders_stg | `LoadDate` | SQL_PROC_INSERT |
| Stage | Orders | source.Orders | `OrderID` | stage.Orders_stg | `SourceOrderID` | SQL_PROC_INSERT |
| Stage | Orders | source.Orders | `CustomerID` | stage.Orders_stg | `SourceCustomerID` | SQL_PROC_INSERT |
| Stage | Orders | source.Orders | `OrderDate` | stage.Orders_stg | `OrderDate` | SQL_PROC_INSERT |
| Stage | Orders | source.Orders | `TotalAmount` | stage.Orders_stg | `TotalAmount` | SQL_PROC_INSERT |
| Stage | Orders | source.Orders | `Status` | stage.Orders_stg | `Status` | SQL_PROC_INSERT |
| Stage | Orders | source.Customers | `CustomerID` | stage.Orders_stg | `LoadStatus` | SQL_PROC_INSERT |
| Stage | Orders | source.Customers | `CustomerID` | stage.Orders_stg | `ErrorMessage` | SQL_PROC_INSERT |
| Stage | Orders | source.Orders | `CustomerID` | stage.Orders_stg | `SourceCustomerID` | SQL_PROC_UPDATE |
| Stage | Orders | source.Orders | `OrderDate` | stage.Orders_stg | `OrderDate` | SQL_PROC_UPDATE |
| Stage | Orders | source.Orders | `TotalAmount` | stage.Orders_stg | `TotalAmount` | SQL_PROC_UPDATE |
| Stage | Orders | source.Orders | `Status` | stage.Orders_stg | `Status` | SQL_PROC_UPDATE |
| Stage | Orders | source.Customers | `CustomerID` | stage.Orders_stg | `LoadStatus` | SQL_PROC_UPDATE |
| Stage | Orders | source.Customers | `CustomerID` | stage.Orders_stg | `ErrorMessage` | SQL_PROC_UPDATE |
