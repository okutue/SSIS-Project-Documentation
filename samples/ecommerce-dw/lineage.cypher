// SSIS Lineage Graph Import Script
// Schema: see docs/neo4j-schema.md in the repository.
// Run in Neo4j Browser or cypher-shell against an empty or dedicated database.
CREATE CONSTRAINT IF NOT EXISTS FOR (p:Package) REQUIRE p.id IS UNIQUE;
CREATE CONSTRAINT IF NOT EXISTS FOR (t:Task) REQUIRE t.id IS UNIQUE;
CREATE CONSTRAINT IF NOT EXISTS FOR (c:Component) REQUIRE c.id IS UNIQUE;

// Packages
MERGE (p:Package {id: '{0B3BB49A-C382-47E0-9A08-B4FF93332480}', name: 'Stage', path: 'C:\Project\SSIS\SSIS_Documentation\SSIS_Documentation\Stage.dtsx'});
MERGE (p:Package {id: '{6CE5C704-B864-4E9D-A608-1ACE53E51E03}', name: 'DW_Load', path: 'C:\Project\SSIS\SSIS_Documentation\SSIS_Documentation\DW_Load.dtsx'});

// Tasks
MERGE (t:Task {id: 'Package\Customers', name: 'Customers', type: 'Microsoft.ExecuteSQLTask'});
MERGE (t:Task {id: 'Package\Load DW Customers', name: 'Load DW Customers', type: 'Microsoft.ExecutePackageTask'});
MERGE (t:Task {id: 'Package\Load Dim Customers', name: 'Load Dim Customers', type: 'Microsoft.Pipeline'});
MERGE (t:Task {id: 'Package\Order Items', name: 'Order Items', type: 'Microsoft.ExecuteSQLTask'});
MERGE (t:Task {id: 'Package\Orders', name: 'Orders', type: 'Microsoft.ExecuteSQLTask'});
MERGE (t:Task {id: 'Package\Start', name: 'Start', type: 'Microsoft.ExecuteSQLTask'});

// Components
MERGE (c:Component {id: 'Package\Customers_sql', name: 'Customers', type: 'Execute SQL Task', sql: 'stage.usp_stage_load_customers'});
MERGE (c:Component {id: 'Package\Load Dim Customers\OLE DB Destination', name: 'OLE DB Destination', type: 'Microsoft.OLEDBDestination', sql: '[DW].[Dim_Customers]'});
MERGE (c:Component {id: 'Package\Load Dim Customers\OLE DB Source', name: 'OLE DB Source', type: 'Microsoft.OLEDBSource', sql: '[stage].[usp_Get_LoadCustomers]'});
MERGE (c:Component {id: 'Package\Order Items_sql', name: 'Order Items', type: 'Execute SQL Task', sql: 'stage.usp_stage_load_orderitems'});
MERGE (c:Component {id: 'Package\Orders_sql', name: 'Orders', type: 'Execute SQL Task', sql: 'stage.usp_stage_load_orders'});
MERGE (c:Component {id: 'Package\Start_sql', name: 'Start', type: 'Execute SQL Task', sql: 'SELECT 1'});

// Task Parent-Child relationships
MATCH (t:Task {id: 'Package\Customers'}), (p:Package {id: '{0B3BB49A-C382-47E0-9A08-B4FF93332480}'}) MERGE (t)-[:BELONGS_TO]->(p);
MATCH (t:Task {id: 'Package\Load DW Customers'}), (p:Package {id: '{0B3BB49A-C382-47E0-9A08-B4FF93332480}'}) MERGE (t)-[:BELONGS_TO]->(p);
MATCH (t:Task {id: 'Package\Load Dim Customers'}), (p:Package {id: '{6CE5C704-B864-4E9D-A608-1ACE53E51E03}'}) MERGE (t)-[:BELONGS_TO]->(p);
MATCH (t:Task {id: 'Package\Order Items'}), (p:Package {id: '{0B3BB49A-C382-47E0-9A08-B4FF93332480}'}) MERGE (t)-[:BELONGS_TO]->(p);
MATCH (t:Task {id: 'Package\Orders'}), (p:Package {id: '{0B3BB49A-C382-47E0-9A08-B4FF93332480}'}) MERGE (t)-[:BELONGS_TO]->(p);
MATCH (t:Task {id: 'Package\Start'}), (p:Package {id: '{0B3BB49A-C382-47E0-9A08-B4FF93332480}'}) MERGE (t)-[:BELONGS_TO]->(p);

// Component Parent-Child relationships
MATCH (c:Component {id: 'Package\Customers_sql'}), (t:Task {id: 'Package\Customers'}) MERGE (c)-[:BELONGS_TO]->(t);
MATCH (c:Component {id: 'Package\Load Dim Customers\OLE DB Destination'}), (t:Task {id: 'Package\Load Dim Customers'}) MERGE (c)-[:BELONGS_TO]->(t);
MATCH (c:Component {id: 'Package\Load Dim Customers\OLE DB Source'}), (t:Task {id: 'Package\Load Dim Customers'}) MERGE (c)-[:BELONGS_TO]->(t);
MATCH (c:Component {id: 'Package\Order Items_sql'}), (t:Task {id: 'Package\Order Items'}) MERGE (c)-[:BELONGS_TO]->(t);
MATCH (c:Component {id: 'Package\Orders_sql'}), (t:Task {id: 'Package\Orders'}) MERGE (c)-[:BELONGS_TO]->(t);
MATCH (c:Component {id: 'Package\Start_sql'}), (t:Task {id: 'Package\Start'}) MERGE (c)-[:BELONGS_TO]->(t);

// Execution Flow
MATCH (t1:Task {id: 'Package\Load DW Customers'}), (t2:Task {id: '{6CE5C704-B864-4E9D-A608-1ACE53E51E03}'}) MERGE (t1)-[:PRECEDES {value: 'Invokes', expr: ''}]->(t2);
MATCH (t1:Task {id: 'Package\Start'}), (t2:Task {id: 'Package\Customers'}) MERGE (t1)-[:PRECEDES {value: 'Success', expr: ''}]->(t2);
MATCH (t1:Task {id: 'Package\Start'}), (t2:Task {id: 'Package\Order Items'}) MERGE (t1)-[:PRECEDES {value: 'Success', expr: ''}]->(t2);
MATCH (t1:Task {id: 'Package\Start'}), (t2:Task {id: 'Package\Orders'}) MERGE (t1)-[:PRECEDES {value: 'Success', expr: ''}]->(t2);
MATCH (t1:Task {id: 'Package\Customers'}), (t2:Task {id: 'Package\Load DW Customers'}) MERGE (t1)-[:PRECEDES {value: 'Success', expr: ''}]->(t2);

// Data Flow connections
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Destination'}) MERGE (c1)-[:FLOWS_TO]->(c2);

// Column-level Lineage mappings
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Destination'}) CREATE (c1)-[:MAPS_TO {srcCol: 'SourceCustomerID', destCol: 'SourceCustomerID', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Destination'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerCode', destCol: 'CustomerCode', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Destination'}) CREATE (c1)-[:MAPS_TO {srcCol: 'FirstName', destCol: 'FirstName', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Destination'}) CREATE (c1)-[:MAPS_TO {srcCol: 'LastName', destCol: 'LastName', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Destination'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Email', destCol: 'Email', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Destination'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Phone', destCol: 'Phone', expr: '', opType: 'XML_FALLBACK'}]->(c2);
MATCH (c1:Component {id: 'Package\Start_sql'}), (c2:Component {id: 'Package\Start_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: '', destCol: '', expr: '1', opType: 'SELECT'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::stage.usp_stage_load_customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: '', destCol: 'LoadBatchID', expr: '@BatchID', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::stage.usp_stage_load_customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: '', destCol: 'LoadDate', expr: 'SYSUTCDATETIME()', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerID', destCol: 'SourceCustomerID', expr: 'c.CustomerID', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerCode', destCol: 'CustomerCode', expr: 'LEFT(LTRIM(RTRIM(c.CustomerCode)), 20)', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'FirstName', destCol: 'FirstName', expr: 'LEFT(LTRIM(RTRIM(c.FirstName)), 50)', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'LastName', destCol: 'LastName', expr: 'LEFT(LTRIM(RTRIM(c.LastName)), 50)', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Email', destCol: 'Email', expr: 'CASE WHEN TRY_CONVERT (NVARCHAR (255), c.Email) IS NULL THEN NULL ELSE LEFT(c.Email, 255) END', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Phone', destCol: 'Phone', expr: 'LEFT(ISNULL(c.Phone, N\'\'), 30)', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::stage.usp_stage_load_customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: '', destCol: 'LoadStatus', expr: 'N\'Loaded\'', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerCode', destCol: 'CustomerCode', expr: 'LEFT(LTRIM(RTRIM(c.CustomerCode)), 20)', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'FirstName', destCol: 'FirstName', expr: 'LEFT(LTRIM(RTRIM(c.FirstName)), 50)', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'LastName', destCol: 'LastName', expr: 'LEFT(LTRIM(RTRIM(c.LastName)), 50)', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Email', destCol: 'Email', expr: 'CASE WHEN TRY_CONVERT (NVARCHAR (255), c.Email) IS NULL THEN NULL ELSE LEFT(c.Email, 255) END', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Customers_sql::source.Customers'}), (c2:Component {id: 'Package\Customers_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Phone', destCol: 'Phone', expr: 'LEFT(ISNULL(c.Phone, N\'\'), 30)', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source::stage.Customers_stg'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Source'}) CREATE (c1)-[:MAPS_TO {srcCol: 'SourceCustomerID', destCol: 'SourceCustomerID', expr: 'SourceCustomerID', opType: 'SQL_PROC_SELECT'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source::stage.Customers_stg'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Source'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerCode', destCol: 'CustomerCode', expr: 'CustomerCode', opType: 'SQL_PROC_SELECT'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source::stage.Customers_stg'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Source'}) CREATE (c1)-[:MAPS_TO {srcCol: 'FirstName', destCol: 'FirstName', expr: 'FirstName', opType: 'SQL_PROC_SELECT'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source::stage.Customers_stg'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Source'}) CREATE (c1)-[:MAPS_TO {srcCol: 'LastName', destCol: 'LastName', expr: 'LastName', opType: 'SQL_PROC_SELECT'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source::stage.Customers_stg'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Source'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Email', destCol: 'Email', expr: 'Email', opType: 'SQL_PROC_SELECT'}]->(c2);
MATCH (c1:Component {id: 'Package\Load Dim Customers\OLE DB Source::stage.Customers_stg'}), (c2:Component {id: 'Package\Load Dim Customers\OLE DB Source'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Phone', destCol: 'Phone', expr: 'Phone', opType: 'SQL_PROC_SELECT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::stage.usp_stage_load_orderitems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: '', destCol: 'LoadBatchID', expr: '@BatchID', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::stage.usp_stage_load_orderitems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: '', destCol: 'LoadDate', expr: 'SYSUTCDATETIME()', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderItemID', destCol: 'SourceOrderItemID', expr: 'oi.OrderItemID', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.Orders'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderID', destCol: 'SourceOrderID', expr: 'COALESCE (o.OrderID, oi.OrderID)', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderID', destCol: 'SourceOrderID', expr: 'COALESCE (o.OrderID, oi.OrderID)', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'ProductCode', destCol: 'ProductCode', expr: 'LEFT(oi.ProductCode, 50)', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Quantity', destCol: 'Quantity', expr: 'oi.Quantity', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'UnitPrice', destCol: 'UnitPrice', expr: 'oi.UnitPrice', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'LineTotal', destCol: 'LineTotal', expr: 'oi.LineTotal', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.Orders'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderID', destCol: 'LoadStatus', expr: 'CASE WHEN o.OrderID IS NULL THEN N\'Error\' ELSE N\'Loaded\' END', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.Orders'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderID', destCol: 'ErrorMessage', expr: 'CASE WHEN o.OrderID IS NULL THEN N\'FK Order not found in source.Orders\' ELSE NULL END', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.Orders'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderID', destCol: 'SourceOrderID', expr: 'COALESCE (o.OrderID, oi.OrderID)', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderID', destCol: 'SourceOrderID', expr: 'COALESCE (o.OrderID, oi.OrderID)', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'ProductCode', destCol: 'ProductCode', expr: 'LEFT(oi.ProductCode, 50)', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Quantity', destCol: 'Quantity', expr: 'oi.Quantity', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'UnitPrice', destCol: 'UnitPrice', expr: 'oi.UnitPrice', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.OrderItems'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'LineTotal', destCol: 'LineTotal', expr: 'oi.LineTotal', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.Orders'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderID', destCol: 'LoadStatus', expr: 'CASE WHEN o.OrderID IS NULL THEN N\'Error\' ELSE N\'Reloaded\' END', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Order Items_sql::source.Orders'}), (c2:Component {id: 'Package\Order Items_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderID', destCol: 'ErrorMessage', expr: 'CASE WHEN o.OrderID IS NULL THEN N\'FK Order not found in source.Orders\' ELSE NULL END', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::stage.usp_stage_load_orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: '', destCol: 'LoadBatchID', expr: '@BatchID', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::stage.usp_stage_load_orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: '', destCol: 'LoadDate', expr: 'SYSUTCDATETIME()', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderID', destCol: 'SourceOrderID', expr: 'o.OrderID', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerID', destCol: 'SourceCustomerID', expr: 'o.CustomerID', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderDate', destCol: 'OrderDate', expr: 'o.OrderDate', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'TotalAmount', destCol: 'TotalAmount', expr: 'o.TotalAmount', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Status', destCol: 'Status', expr: 'LEFT(o.Status, 20)', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Customers'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerID', destCol: 'LoadStatus', expr: 'CASE WHEN c.CustomerID IS NULL THEN N\'Error\' ELSE N\'Loaded\' END', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Customers'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerID', destCol: 'ErrorMessage', expr: 'CASE WHEN c.CustomerID IS NULL THEN N\'FK Customer not found in source.Customers\' ELSE NULL END', opType: 'SQL_PROC_INSERT'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerID', destCol: 'SourceCustomerID', expr: 'o.CustomerID', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'OrderDate', destCol: 'OrderDate', expr: 'o.OrderDate', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'TotalAmount', destCol: 'TotalAmount', expr: 'o.TotalAmount', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Orders'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'Status', destCol: 'Status', expr: 'LEFT(o.Status, 20)', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Customers'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerID', destCol: 'LoadStatus', expr: 'CASE WHEN c.CustomerID IS NULL THEN N\'Error\' ELSE N\'Reloaded\' END', opType: 'SQL_PROC_UPDATE'}]->(c2);
MATCH (c1:Component {id: 'Package\Orders_sql::source.Customers'}), (c2:Component {id: 'Package\Orders_sql'}) CREATE (c1)-[:MAPS_TO {srcCol: 'CustomerID', destCol: 'ErrorMessage', expr: 'CASE WHEN c.CustomerID IS NULL THEN N\'FK Customer not found in source.Customers\' ELSE NULL END', opType: 'SQL_PROC_UPDATE'}]->(c2);
