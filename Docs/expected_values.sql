-- StockWise: verification queries for the 10-question evaluation and acceptance tests.
-- Run against the FINAL seeded database, before asking the chatbot, and record results
-- as the "Expected fact" column in the report. Adjust column names to the final schema.
-- Business rules: low stock = StockQuantity <= ReorderLevel; value = SUM(StockQuantity * UnitCost).

-- A2: seed counts and FK integrity
SELECT (SELECT COUNT(*) FROM Suppliers) AS Suppliers, (SELECT COUNT(*) FROM Products) AS Products;
PRAGMA foreign_key_check;

-- Q1: Find SKU P001 and show its supplier, quantity and cost.  (get_product_by_sku)
SELECT p.SKU, p.Name, s.Name AS Supplier, p.StockQuantity, p.UnitCost
FROM Products p JOIN Suppliers s ON s.SupplierId = p.SupplierId
WHERE p.SKU = 'P001';

-- Q2: Show all low-stock products.  (search_inventory; tool caps output at 20 rows)
-- Q7: How many products are at or below their reorder level?  (summarize_inventory)
SELECT COUNT(*) AS LowStockCount FROM Products WHERE StockQuantity <= ReorderLevel;
SELECT SKU, Name, StockQuantity, ReorderLevel FROM Products
WHERE StockQuantity <= ReorderLevel ORDER BY SKU;

-- Q3: Show low-stock items in the Stationery category.  (search_inventory)
SELECT SKU, Name, StockQuantity, ReorderLevel FROM Products
WHERE Category = 'Stationery' AND StockQuantity <= ReorderLevel ORDER BY SKU;

-- Q4: Find products supplied by Supplier S01.  (search_inventory)
SELECT p.SKU, p.Name FROM Products p
WHERE p.SupplierId = 'S01' ORDER BY p.SKU;   -- adjust if SupplierId is an integer

-- Q5: Search for products containing "paper".  (search_inventory)
SELECT SKU, Name FROM Products WHERE Name LIKE '%paper%' ORDER BY SKU;

-- Q6: What is the total inventory value at cost?  (summarize_inventory)
SELECT ROUND(SUM(StockQuantity * UnitCost), 2) AS InventoryValueAtCost FROM Products;

-- Q8: Rank the top five categories by inventory value.  (rank_inventory_categories)
-- Q9: Which category has the lowest inventory value?  (last row of the full ranking)
SELECT Category, ROUND(SUM(StockQuantity * UnitCost), 2) AS Value
FROM Products GROUP BY Category ORDER BY Value DESC, Category;

-- Q10: Look up SKU ZZZ999.  (get_product_by_sku -> honest "not found")
SELECT COUNT(*) AS Matches FROM Products WHERE SKU = 'ZZZ999';   -- expect 0

-- A7 / S01-S02: run BEFORE and AFTER the SQL-injection tests; counts must match.
SELECT (SELECT COUNT(*) FROM Suppliers) AS Suppliers, (SELECT COUNT(*) FROM Products) AS Products;
