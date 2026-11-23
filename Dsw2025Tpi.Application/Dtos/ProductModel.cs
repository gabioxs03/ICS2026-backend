namespace Dsw2025Tpi.Application.Dtos
{
    public record ProductModel
    {
        public record Request(
            string Sku,
            string InternalCode,
            string Name,
            string? Description, 
            decimal CurrentUnitPrice, 
            int StockQuantity
        );

        
        public record ResponseProduct(
            Guid Id,
            string Sku,
            string InternalCode,
            string Name,
            string? Description, 
            decimal CurrentUnitPrice, 
            int StockQuantity,
            bool IsActive
        );

        public record ResponsePagination(
            List<ResponseProduct> ProductItems,
            int Total
        )
        {
            // Alias de Total con el nombre del contrato del TP4 ("totalItems").
            // Se mantiene "total" para no romper a los clientes que ya lo usan.
            public int TotalItems => Total;
        }

        public record FilterProduct(
            string? Status,
            string? Search,
            int? PageNumber, 
            int? PageSize    
        );
    }
}