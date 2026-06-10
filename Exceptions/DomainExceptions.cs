namespace CareerHub.Api.Exceptions;

/// <summary>A requested resource does not exist -> HTTP 404.</summary>
public class NotFoundException(string message) : Exception(message);

/// <summary>A uniqueness/state conflict, e.g. an email already registered -> HTTP 409.</summary>
public class ConflictException(string message) : Exception(message);

/// <summary>An applicant applied twice to the same listing -> HTTP 409.</summary>
public class DuplicateApplicationException(string message) : Exception(message);

/// <summary>Invalid credentials -> HTTP 401. Deliberately vague (see AuthService).</summary>
public class UnauthorizedException(string message) : Exception(message);
