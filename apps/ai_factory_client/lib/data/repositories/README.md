# data/repositories

Concrete repositories. They call `ApiClient`, map DTOs to domain models via
`data/mappers`, translate failures into `AppError`, and never return a DTO.
Added in Phase 19.2.
