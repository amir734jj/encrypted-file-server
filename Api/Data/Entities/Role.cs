using Api.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Api.Data.Entities;

public class Role : IdentityRole<Guid>, IEntity;
